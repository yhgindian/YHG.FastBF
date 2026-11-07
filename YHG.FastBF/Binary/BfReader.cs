using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using YHG.FastBF.Attributes;

namespace YHG.FastBF.Binary
{
    public class BfReader : BinaryReader
    {
        #region Internal Definitions

        private sealed class NrbfRef
        {
            public readonly int Id;
            public NrbfRef(int id) => Id = id;
        }

        private sealed class ClassInfo
        {
            public string Name = string.Empty;
            public string[] MemberNames = Array.Empty<string>();
            public byte[] Types = Array.Empty<byte>();
            public object?[] Additional = Array.Empty<object?>();
        }

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();

        private readonly Dictionary<int, object> _objects = new();
        private readonly Dictionary<int, ClassInfo> _classInfos = new();
        private readonly Dictionary<int, string> _libraries = new();
        private int _pendingNulls;

        #endregion

        #region Properties & Bindings

        public NrbfBindToTypeBinderDelegate? Binder { get; set; }

        public int RootId { get; private set; }
        public int HeaderId { get; private set; }
        public int MajorVersion { get; private set; }
        public int MinorVersion { get; private set; }

        #endregion

        #region Constructors

        public BfReader(Stream input) : base(input, Encoding.UTF8, leaveOpen: false) { }
        public BfReader(Stream input, Encoding encoding, bool leaveOpen) : base(input, encoding, leaveOpen) { }

        #endregion

        #region Public API

        /// <summary>
        /// Reads the root object directly as a raw, untyped NrbfClass record graph.
        /// Zero reflection overhead and 100% Native AOT Compatible.
        /// </summary>
        public NrbfClass ReadRawRoot()
        {
            ParseStream();

            if (_objects.TryGetValue(RootId, out object? resolvedRoot) && resolvedRoot is NrbfClass rc)
                return rc;

            throw new InvalidDataException("No root class record found in the NRBF stream.");
        }

        /// <summary>
        /// Reads the root object into a strongly typed target instance.
        /// Prefers IBfReadable for zero-reflection fast hydration, falling back to cached field binding.
        /// </summary>
        public T ReadRootClass<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() where T : new()
        {
            ParseStream();

            if (!_objects.TryGetValue(RootId, out object? resolvedRoot) || resolvedRoot is not NrbfClass rc)
                throw new InvalidDataException("No root class record found in the NRBF stream.");

            return (T)CastToType(typeof(T), rc)!;
        }

        #endregion

        #region Stream Parser

        private void ParseStream()
        {
            _objects.Clear();
            _classInfos.Clear();
            _libraries.Clear();
            _pendingNulls = 0;

            long start = BaseStream.Position;
            byte header = ReadByte();
            if (header != 0x00)
                throw new InvalidDataException($"Invalid NRBF stream: expected header 0x00, got 0x{header:X2} at offset {start}.");

            RootId = ReadInt32();
            HeaderId = ReadInt32();
            MajorVersion = ReadInt32();
            MinorVersion = ReadInt32();

            while (BaseStream.Position < BaseStream.Length)
            {
                byte tag = ReadByte();
                if (tag == 0x0B) break;
                ReadRecord(tag);
            }

            ResolveReferences();
        }

        private object? NextValue()
        {
            if (_pendingNulls > 0)
            {
                _pendingNulls--;
                return null;
            }

            return ReadRecord(ReadByte());
        }

        private object? ReadRecord(byte tag)
        {
            long pos = BaseStream.Position - 1;

            switch (tag)
            {
                case 0x01:
                case 0x04:
                case 0x05:
                    return ReadClass(tag);

                case 0x06:
                    {
                        int id = ReadInt32();
                        string s = ReadString();
                        _objects[id] = s;
                        return s;
                    }

                case 0x07:
                    return ReadBinaryArray();

                case 0x08:
                    return ReadPrimitive(ReadByte());

                case 0x09:
                    return new NrbfRef(ReadInt32());

                case 0x0A:
                    return null;

                case 0x0C:
                    {
                        int libId = ReadInt32();
                        _libraries[libId] = ReadString();
                        return ReadRecord(ReadByte());
                    }

                case 0x0D:
                    _pendingNulls = Math.Max(0, ReadByte() - 1);
                    return null;

                case 0x0E:
                    _pendingNulls = Math.Max(0, ReadInt32() - 1);
                    return null;

                case 0x0F:
                    {
                        int id = ReadInt32();
                        int len = ReadInt32();
                        byte prim = ReadByte();
                        object arr = ReadPrimitiveArray(len, prim);
                        _objects[id] = arr;
                        return arr;
                    }

                case 0x10:
                case 0x11:
                    {
                        int id = ReadInt32();
                        int len = ReadInt32();
                        if (len < 0) throw new InvalidDataException($"Negative array length encountered at stream offset {pos}.");

                        var arr = new object?[len];
                        _objects[id] = arr;
                        for (int i = 0; i < len; i++)
                            arr[i] = NextValue();

                        return arr;
                    }

                default:
                    throw new InvalidDataException($"Unknown NRBF record tag 0x{tag:X2} at stream offset {pos}.");
            }
        }

        private NrbfClass ReadClass(byte tag)
        {
            int objectId = ReadInt32();
            ClassInfo info;

            if (tag == 0x01)
            {
                int metaId = ReadInt32();
                if (!_classInfos.TryGetValue(metaId, out info!))
                    throw new InvalidDataException($"ClassWithId record references unindexed metadata ID {metaId}.");
            }
            else
            {
                string rawName = ReadString();
                int count = ReadInt32();
                if (count < 0) throw new InvalidDataException($"Negative member count for type '{rawName}'.");

                var names = new string[count];
                for (int i = 0; i < count; i++)
                    names[i] = ReadString();

                byte[] types = ReadBytes(count);
                var additional = new object?[count];
                for (int i = 0; i < count; i++)
                    additional[i] = ReadAdditionalInfo(types[i]);

                string? asmName = null;
                if (tag == 0x05)
                {
                    int lId = ReadInt32();
                    _libraries.TryGetValue(lId, out asmName);
                }

                Binder?.Invoke(ref asmName, ref rawName);

                info = new ClassInfo
                {
                    Name = rawName,
                    MemberNames = names,
                    Types = types,
                    Additional = additional
                };

                _classInfos[objectId] = info;
            }

            var cls = new NrbfClass
            {
                ObjectId = objectId,
                ClassName = info.Name,
                MemberNames = info.MemberNames,
                Values = new object?[info.MemberNames.Length]
            };

            _objects[objectId] = cls;

            for (int i = 0; i < info.MemberNames.Length; i++)
            {
                cls.Values[i] = info.Types[i] == 0
                    ? ReadPrimitive((byte)info.Additional[i]!)
                    : NextValue();
            }

            return cls;
        }

        private object? ReadAdditionalInfo(byte binaryType)
        {
            if (binaryType == 0 || binaryType == 7)
                return ReadByte();

            if (binaryType == 3)
            {
                string tName = ReadString();
                string? aName = null;
                Binder?.Invoke(ref aName, ref tName);
                return tName;
            }

            if (binaryType == 4)
            {
                string tName = ReadString();
                int lId = ReadInt32();
                string? aName = _libraries.TryGetValue(lId, out var lib) ? lib : null;
                Binder?.Invoke(ref aName, ref tName);
                return (tName, lId);
            }

            return null;
        }

        private object ReadBinaryArray()
        {
            int id = ReadInt32();
            byte arrayType = ReadByte();
            int rank = ReadInt32();

            int length = 1;
            for (int r = 0; r < rank; r++)
                length *= ReadInt32();

            if (arrayType >= 3)
            {
                for (int r = 0; r < rank; r++)
                    ReadInt32();
            }

            byte binaryType = ReadByte();
            object? additional = ReadAdditionalInfo(binaryType);

            if (binaryType == 0)
            {
                object primArr = ReadPrimitiveArray(length, (byte)additional!);
                _objects[id] = primArr;
                return primArr;
            }

            var arr = new object?[length];
            _objects[id] = arr;
            for (int i = 0; i < length; i++)
                arr[i] = NextValue();

            return arr;
        }

        private object? ReadPrimitive(byte code) => code switch
        {
            1 => ReadBoolean(),
            2 => ReadByte(),
            3 => ReadChar(),
            5 => decimal.Parse(ReadString(), CultureInfo.InvariantCulture),
            6 => ReadDouble(),
            7 => ReadInt16(),
            8 => ReadInt32(),
            9 => ReadInt64(),
            10 => ReadSByte(),
            11 => ReadSingle(),
            12 => TimeSpan.FromTicks(ReadInt64()),
            13 => ReadDateTime(),
            14 => ReadUInt16(),
            15 => ReadUInt32(),
            16 => ReadUInt64(),
            17 => null,
            18 => ReadString(),
            _ => (object)ReadInt32()
        };

        private DateTime ReadDateTime()
        {
            long v = ReadInt64();
            return new DateTime(v & 0x3FFFFFFFFFFFFFFF, (DateTimeKind)(int)((ulong)v >> 62));
        }

        private object ReadPrimitiveArray(int len, byte code)
        {
            if (len < 0) throw new InvalidDataException("Negative primitive array length.");

            return code switch
            {
                1 => ReadBools(len),
                2 => ReadBytes(len),
                6 => ReadDoubles(len),
                7 => ReadInt16s(len),
                8 => ReadInt32s(len),
                9 => ReadInt64s(len),
                11 => ReadSingles(len),
                _ => ReadGenericPrimitives(len, code)
            };
        }

        private bool[] ReadBools(int len) { var a = new bool[len]; for (int i = 0; i < len; i++) a[i] = ReadBoolean(); return a; }
        private double[] ReadDoubles(int len) { var a = new double[len]; for (int i = 0; i < len; i++) a[i] = ReadDouble(); return a; }
        private short[] ReadInt16s(int len) { var a = new short[len]; for (int i = 0; i < len; i++) a[i] = ReadInt16(); return a; }
        private int[] ReadInt32s(int len) { var a = new int[len]; for (int i = 0; i < len; i++) a[i] = ReadInt32(); return a; }
        private long[] ReadInt64s(int len) { var a = new long[len]; for (int i = 0; i < len; i++) a[i] = ReadInt64(); return a; }
        private float[] ReadSingles(int len) { var a = new float[len]; for (int i = 0; i < len; i++) a[i] = ReadSingle(); return a; }
        private object?[] ReadGenericPrimitives(int len, byte code) { var a = new object?[len]; for (int i = 0; i < len; i++) a[i] = ReadPrimitive(code); return a; }

        private void ResolveReferences()
        {
            foreach (object o in _objects.Values)
            {
                if (o is NrbfClass c)
                {
                    for (int i = 0; i < c.Values.Length; i++)
                        c.Values[i] = Fix(c.Values[i]);
                }
                else if (o is object?[] a)
                {
                    for (int i = 0; i < a.Length; i++)
                        a[i] = Fix(a[i]);
                }
            }
        }

        private object? Fix(object? v) =>
            v is NrbfRef r ? (_objects.TryGetValue(r.Id, out object? target) ? target : null) : v;

        #endregion

        #region Native AOT Deserialization Engine

        [UnconditionalSuppressMessage("AotTrimming", "IL2075:ReflectionUsed", Justification = "Preserved dynamically via DynamicallyAccessedMembers attribute annotations")]
        private object? CastToType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type targetType, object? val)
        {
            if (val == null) return null;

            if (targetType.IsEnum)
                return Enum.ToObject(targetType, NrbfClass.ToInt(val));

            if (targetType == typeof(int)) return NrbfClass.ToInt(val);
            if (targetType == typeof(float)) return NrbfClass.ToFloat(val);
            if (targetType == typeof(bool)) return NrbfClass.ToBool(val);
            if (targetType == typeof(string)) return val.ToString();

            if (targetType.IsArray)
            {
                Type elemType = targetType.GetElementType()!;
                if (val is Array rawArr && targetType.IsInstanceOfType(val)) return val;

                if (val is object?[] objArr)
                {
                    try
                    {
                        Array createdArr = Array.CreateInstance(elemType, objArr.Length);
                        for (int i = 0; i < objArr.Length; i++)
                            createdArr.SetValue(CastToType(elemType, objArr[i]), i);

                        return createdArr;
                    }
                    catch
                    {
                        return objArr;
                    }
                }
            }

            if (val is not NrbfClass nrbf)
            {
                if (targetType.IsInstanceOfType(val)) return val;
                try { return Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture); } catch { return val; }
            }

            if (IsDictionaryType(targetType))
                return HydrateDictionary(targetType, nrbf);

            object instance;
            try
            {
                instance = Activator.CreateInstance(targetType)!;
            }
            catch
            {
                instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(targetType);
            }

            // Zero-Reflection Interface Fast-Path
            if (instance is IBfReadable readable)
            {
                readable.ReadBf(nrbf);

                if (instance is IBfDeserialized cb)
                    cb.OnDeserialized();

                return instance;
            }

            // Fallback cached reflection path
            FieldInfo[] fields = FieldCache.GetOrAdd(targetType, static t =>
                t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

            for (int i = 0; i < nrbf.MemberNames.Length; i++)
            {
                string mName = nrbf.MemberNames[i];
                object? mVal = nrbf.Values[i];
                if (mVal == null) continue;

                FieldInfo? matchedField = Array.Find(fields, f =>
                {
                    if (f.GetCustomAttribute<BfIgnoreAttribute>() != null)
                        return false;

                    var propAttr = f.GetCustomAttribute<BfPropertyAttribute>();
                    if (propAttr != null && string.Equals(propAttr.Name, mName, StringComparison.OrdinalIgnoreCase))
                        return true;

                    return string.Equals(f.Name, mName, StringComparison.OrdinalIgnoreCase)
                        || f.Name.EndsWith("+" + mName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(f.Name, "<" + mName + ">k__BackingField", StringComparison.OrdinalIgnoreCase);
                });

                if (matchedField != null)
                {
                    try
                    {
                        object? converted = CastToType(matchedField.FieldType, mVal);
                        matchedField.SetValue(instance, converted);
                    }
                    catch { }
                }
            }

            if (instance is IBfDeserialized deserializedCallback)
            {
                deserializedCallback.OnDeserialized();
            }

            return instance;
        }

        private static bool IsDictionaryType(Type type)
        {
            if (type.IsGenericType)
            {
                Type def = type.GetGenericTypeDefinition();
                if (def == typeof(Dictionary<,>) || def.Name.Contains("CustomDictionary"))
                    return true;
            }

            return typeof(IDictionary).IsAssignableFrom(type);
        }

        private object HydrateDictionary(Type targetType, NrbfClass nrbf)
        {
            object dictInstance;
            try
            {
                dictInstance = Activator.CreateInstance(targetType)!;
            }
            catch
            {
                dictInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(targetType);
            }

            if (dictInstance is IDictionary dict)
            {
                Type[] genArgs = targetType.IsGenericType ? targetType.GetGenericArguments() : Array.Empty<Type>();
                Type keyType = genArgs.Length > 0 ? genArgs[0] : typeof(object);
                Type valType = genArgs.Length > 1 ? genArgs[1] : typeof(object);

                foreach (var (k, v) in nrbf.GetDictionaryPairs())
                {
                    if (k == null) continue;

                    try
                    {
                        object? cKey = CastToType(keyType, k);
                        object? cVal = CastToType(valType, v);
                        dict[cKey!] = cVal;
                    }
                    catch { }
                }
            }

            return dictInstance;
        }

        #endregion
    }
}