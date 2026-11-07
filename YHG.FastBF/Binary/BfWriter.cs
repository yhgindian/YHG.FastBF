using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using YHG.FastBF.Attributes;

namespace YHG.FastBF.Binary
{
    public class BfWriter : BinaryWriter
    {
        private const string MsCorlibName = "mscorlib";
        private const string MsCorlibFull = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        private static readonly Regex CoreLibRegex = new(
            @"System\.Private\.CoreLib, Version=[\d.]+, Culture=neutral, PublicKeyToken=7cec85d7bea7798e",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private sealed class NameInfo
        {
            public string TypeName = string.Empty;
            public string Assembly = string.Empty;
            public bool IsSystem;
        }

        private sealed class TypeDesc
        {
            public byte Binary;
            public byte Prim;
            public string? Name;
            public string? Assembly;
            public Type? Clr;
        }

        private sealed class Shape
        {
            public string TypeName;
            public string Assembly;
            public bool IsSystem;
            public string[] Names;
            public TypeDesc[] Members;
            public Func<object, object?[]>? ValueGetter;
            public bool IsEnum;

            public Shape(string typeName, string assembly, bool isSystem, string[] names, TypeDesc[] members, Func<object, object?[]>? valueGetter)
            {
                TypeName = typeName;
                Assembly = assembly;
                IsSystem = isSystem;
                Names = names;
                Members = members;
                ValueGetter = valueGetter;
            }
        }

        private sealed class DictInfo
        {
            public Shape Dict = null!;
            public Shape Kvp = null!;
            public Type KvpType = null!;
            public object Comparer = null!;
        }

        private sealed class KvpArray
        {
            public readonly DictInfo Info;
            public readonly List<KeyValuePair<object?, object?>> Items;

            public KvpArray(DictInfo info, List<KeyValuePair<object?, object?>> items)
            {
                Info = info;
                Items = items;
            }
        }

        private int _nextId = 1;
        private readonly Dictionary<string, int> _libraries = new(StringComparer.Ordinal);
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Shape, int> _metaIds = new();
        private readonly Queue<(object Obj, int Id)> _queue = new();

        private readonly Dictionary<Type, NameInfo> _nameCache = new();
        private readonly Dictionary<Type, TypeDesc> _descCache = new();
        private readonly Dictionary<Type, Shape> _shapeCache = new();
        private readonly Dictionary<Type, DictInfo> _dictCache = new();
        private readonly Dictionary<Type, object> _comparerCache = new();

        public NrbfBindToNameBinderDelegate? Binder { get; set; } = null;

        public BfWriter(Stream output) : base(output, Encoding.UTF8, leaveOpen: false) { }

        public BfWriter(Stream output, Encoding encoding, bool leaveOpen) : base(output, encoding, leaveOpen) { }

        private void WriteBasicBfData()
        {
            Write((byte)0x00);
            Write(1);
            Write(-1);
            Write(1);
            Write(0);
        }

        public int WriteAssemblyName(string assemblyName)
        {
            ArgumentNullException.ThrowIfNull(assemblyName);
            return EnsureLibrary(assemblyName);
        }

        public void WriteRootClass<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T rootInstance) where T : notnull
        {
            ArgumentNullException.ThrowIfNull(rootInstance);

            _nextId = 1;
            _libraries.Clear();
            _ids.Clear();
            _metaIds.Clear();
            _queue.Clear();

            WriteBasicBfData();

            object root = rootInstance;
            int rootId = _nextId++;
            _ids[root] = rootId;
            WriteObjectRecord(root, rootId);

            while (_queue.Count > 0)
            {
                var (obj, id) = _queue.Dequeue();
                WriteObjectRecord(obj, id);
            }

            Write((byte)0x0B);
            Flush();
        }

        private int EnsureLibrary(string assemblyName)
        {
            if (_libraries.TryGetValue(assemblyName, out int id)) return id;

            id = _nextId++;
            _libraries[assemblyName] = id;

            Write((byte)0x0C);
            Write(id);
            Write(assemblyName);
            return id;
        }

        private int Schedule(object obj)
        {
            if (_ids.TryGetValue(obj, out int id)) return id;
            id = _nextId++;
            _ids[obj] = id;
            _queue.Enqueue((obj, id));
            return id;
        }

        private void WriteObjectRecord(object obj, int id)
        {
            switch (obj)
            {
                case KvpArray ka:
                    WriteKvpArray(ka, id);
                    return;
                case Array arr:
                    WriteArrayRecord(arr, id);
                    return;
            }

            Type t = obj.GetType();
            if (!t.IsValueType && IsDictionaryType(t))
            {
                WriteDictionaryRecord(obj, t, id);
                return;
            }

            Shape shape = GetShape(t);
            WriteClassRecord(shape, id, ValuesOf(shape, obj));
        }

        private static object?[] ValuesOf(Shape shape, object obj)
        {
            if (shape.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(obj.GetType());
                return new[] { Convert.ChangeType(obj, underlying) };
            }

            return shape.ValueGetter!(obj);
        }

        private void WriteClassRecord(Shape shape, int objectId, object?[] values)
        {
            int n = shape.Members.Length;

            if (_metaIds.TryGetValue(shape, out int metaId))
            {
                Write((byte)0x01);
                Write(objectId);
                Write(metaId);
            }
            else
            {
                int ownLib = shape.IsSystem ? 0 : EnsureLibrary(shape.Assembly);
                var memberLibs = new int[n];
                for (int i = 0; i < n; i++)
                {
                    if (shape.Members[i].Binary == 4)
                        memberLibs[i] = EnsureLibrary(shape.Members[i].Assembly!);
                }

                Write(shape.IsSystem ? (byte)0x04 : (byte)0x05);
                Write(objectId);
                Write(shape.TypeName);
                Write(n);

                for (int i = 0; i < n; i++) Write(shape.Names[i]);
                for (int i = 0; i < n; i++) Write(shape.Members[i].Binary);
                for (int i = 0; i < n; i++)
                {
                    TypeDesc d = shape.Members[i];
                    switch (d.Binary)
                    {
                        case 0:
                        case 7:
                            Write(d.Prim);
                            break;
                        case 3:
                            Write(d.Name!);
                            break;
                        case 4:
                            Write(d.Name!);
                            Write(memberLibs[i]);
                            break;
                    }
                }

                if (!shape.IsSystem) Write(ownLib);

                _metaIds[shape] = objectId;
            }

            for (int i = 0; i < n; i++)
                WriteMember(shape.Members[i], values[i]);
        }

        private void WriteMember(TypeDesc desc, object? val)
        {
            if (desc.Binary == 0)
            {
                WritePrimitive(desc.Prim, val ?? 0);
                return;
            }
            WriteRecordValue(val, desc.Clr);
        }

        private void WriteRecordValue(object? val, Type? declared)
        {
            if (val == null)
            {
                Write((byte)0x0A);
                return;
            }

            if (val is string s)
            {
                WriteStringValue(s);
                return;
            }

            Type t = val.GetType();

            if (TryGetPrimitiveCode(t, out byte code))
            {
                Write((byte)0x08);
                Write(code);
                WritePrimitive(code, val);
                return;
            }

            if (t.IsValueType && declared == t)
            {
                Shape sh = GetShape(t);
                WriteClassRecord(sh, _nextId++, ValuesOf(sh, val));
                return;
            }

            Write((byte)0x09);
            Write(Schedule(val));
        }

        private void WriteStringValue(string s)
        {
            if (_ids.TryGetValue(s, out int existing))
            {
                Write((byte)0x09);
                Write(existing);
                return;
            }

            int id = _nextId++;
            _ids[s] = id;
            Write((byte)0x06);
            Write(id);
            Write(s);
        }

        private void WriteDictionaryRecord(object dict, Type dictType, int id)
        {
            DictInfo info = GetDictInfo(dictType);

            var items = new List<KeyValuePair<object?, object?>>();
            foreach (var kv in EnumeratePairs(dict)) items.Add(kv);

            int count = items.Count;
            var ka = new KvpArray(info, items);

            WriteClassRecord(info.Dict, id, new object?[]
            {
                count,
                info.Comparer,
                GetNextPrime(count),
                ka
            });
        }

        private static readonly int[] HashPrimes =
        {
            11, 19, 37, 73, 109, 163, 251, 367, 557, 823, 1237, 1861, 2777, 4177, 6247, 9371, 14057, 21089,
            31627, 47431, 71143, 106721, 160073, 240101, 360163, 540217, 810343, 1215497, 1823231, 2734867,
            4102283, 6153409, 9230113, 13845163
        };

        private static int GetNextPrime(int value)
        {
            for (int i = 0; i < HashPrimes.Length; i++)
            {
                int p = HashPrimes[i];
                if (value <= p) return p;
            }
            return value | 1;
        }

        private void WriteKvpArray(KvpArray ka, int id)
        {
            NameInfo kn = ResolveName(ka.Info.KvpType);
            int libId = kn.IsSystem ? 0 : EnsureLibrary(kn.Assembly);

            Write((byte)0x07);
            Write(id);
            Write((byte)0);
            Write(1);
            Write(ka.Items.Count);
            Write(kn.IsSystem ? (byte)3 : (byte)4);
            Write(kn.TypeName);
            if (!kn.IsSystem) Write(libId);

            foreach (var item in ka.Items)
                WriteClassRecord(ka.Info.Kvp, _nextId++, new object?[] { item.Key, item.Value });
        }

        private object GetComparer(Type keyType)
        {
            if (_comparerCache.TryGetValue(keyType, out var c)) return c;
            c = typeof(EqualityComparer<>).MakeGenericType(keyType)
                    .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null)!;
            _comparerCache[keyType] = c;
            return c;
        }

        private DictInfo GetDictInfo(Type dictType)
        {
            if (_dictCache.TryGetValue(dictType, out var cached)) return cached;

            Type[] kv = GetKeyValueTypes(dictType);
            Type keyType = kv[0], valueType = kv[1];
            Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
            object comparer = GetComparer(keyType);

            NameInfo dn = ResolveName(dictType);
            NameInfo kn = ResolveName(kvpType);

            var info = new DictInfo
            {
                Comparer = comparer,
                KvpType = kvpType,
                Dict = new Shape(dn.TypeName, dn.Assembly, dn.IsSystem,
                    new[] { "Version", "Comparer", "HashSize", "KeyValuePairs" },
                    new[]
                    {
                        PrimDesc(8),
                        Describe(comparer.GetType()),
                        PrimDesc(8),
                        Describe(kvpType.MakeArrayType())
                    },
                    null),
                Kvp = new Shape(kn.TypeName, kn.Assembly, kn.IsSystem,
                    new[] { "key", "value" },
                    new[] { Describe(keyType), Describe(valueType) },
                    null)
            };

            _dictCache[dictType] = info;
            return info;
        }

        private static Type[] GetKeyValueTypes(Type t)
        {
            if (t.IsGenericType)
            {
                Type[] a = t.GetGenericArguments();
                if (a.Length == 2) return a;
            }
            foreach (Type i in t.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    return i.GetGenericArguments();
            }
            return new[] { typeof(object), typeof(object) };
        }

        private static IEnumerable<KeyValuePair<object?, object?>> EnumeratePairs(object dict)
        {
            if (dict is IDictionary d)
            {
                foreach (DictionaryEntry e in d)
                    yield return new KeyValuePair<object?, object?>(e.Key, e.Value);
                yield break;
            }

            if (dict is IEnumerable en)
            {
                foreach (object? item in en)
                {
                    if (item == null) continue;
                    Type it = item.GetType();
                    yield return new KeyValuePair<object?, object?>(
                        it.GetProperty("Key")?.GetValue(item),
                        it.GetProperty("Value")?.GetValue(item));
                }
            }
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

        private void WriteArrayRecord(Array arr, int id)
        {
            Type at = arr.GetType();
            if (at.GetArrayRank() != 1 || arr.GetLowerBound(0) != 0)
                throw new NotSupportedException($"Multi-dimensional / non-zero-based arrays not supported: {at}");

            Type elem = at.GetElementType()!;
            int len = arr.Length;

            if (TryGetPrimitiveCode(elem, out byte pc))
            {
                Write((byte)0x0F);
                Write(id);
                Write(len);
                Write(pc);
                if (arr is byte[] bytes) Write(bytes);
                else for (int i = 0; i < len; i++) WritePrimitive(pc, arr.GetValue(i)!);
                return;
            }

            if (elem == typeof(string))
            {
                Write((byte)0x11);
                Write(id);
                Write(len);
                WriteItems(arr, elem);
                return;
            }

            if (elem == typeof(object))
            {
                Write((byte)0x10);
                Write(id);
                Write(len);
                WriteItems(arr, elem);
                return;
            }

            TypeDesc td = Describe(elem);
            int libId = td.Binary == 4 ? EnsureLibrary(td.Assembly!) : 0;

            Write((byte)0x07);
            Write(id);
            Write(elem.IsArray ? (byte)1 : (byte)0);
            Write(1);
            Write(len);
            Write(td.Binary);
            switch (td.Binary)
            {
                case 0:
                case 7: Write(td.Prim); break;
                case 3: Write(td.Name!); break;
                case 4: Write(td.Name!); Write(libId); break;
            }
            WriteItems(arr, elem);
        }

        private void WriteItems(Array arr, Type declared)
        {
            int nulls = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                object? item = arr.GetValue(i);
                if (item == null) { nulls++; continue; }
                FlushNulls(ref nulls);
                WriteRecordValue(item, declared);
            }
            FlushNulls(ref nulls);
        }

        private void FlushNulls(ref int nulls)
        {
            if (nulls == 0) return;
            if (nulls == 1) Write((byte)0x0A);
            else if (nulls < 256) { Write((byte)0x0D); Write((byte)nulls); }
            else { Write((byte)0x0E); Write(nulls); }
            nulls = 0;
        }

        private static bool IsCoreLib(Assembly a) => a == typeof(object).Assembly;

        private static bool IsSystemAssemblyName(string asm) =>
            asm == MsCorlibName || asm.StartsWith(MsCorlibName + ",", StringComparison.Ordinal);

        private static string RawName(Type type)
        {
            string n = type.FullName ?? type.Name;
            return CoreLibRegex.Replace(n, MsCorlibFull);
        }

        private static string RuntimeAssemblyName(Type type) =>
            IsCoreLib(type.Assembly) ? MsCorlibName : (type.Assembly.FullName ?? type.Assembly.GetName().Name ?? "");

        private NameInfo ResolveName(Type type)
        {
            if (_nameCache.TryGetValue(type, out NameInfo? cached)) return cached;

            string typeName = RawName(type);
            string? asm = RuntimeAssemblyName(type);

            var bfTypeAttr = type.GetCustomAttribute<BfTypeAttribute>();
            if (bfTypeAttr != null)
            {
                if (!string.IsNullOrEmpty(bfTypeAttr.TypeName)) typeName = bfTypeAttr.TypeName;
                if (!string.IsNullOrEmpty(bfTypeAttr.AssemblyName)) asm = bfTypeAttr.AssemblyName;
            }

            Binder?.Invoke(type, ref asm, ref typeName);

            asm ??= RuntimeAssemblyName(type);

            var info = new NameInfo
            {
                TypeName = typeName,
                Assembly = asm,
                IsSystem = IsSystemAssemblyName(asm)
            };

            _nameCache[type] = info;
            return info;
        }

        private static TypeDesc PrimDesc(byte code) => new TypeDesc { Binary = 0, Prim = code };

        private TypeDesc Describe(Type t)
        {
            if (_descCache.TryGetValue(t, out TypeDesc? d)) return d;

            if (t == typeof(string)) d = new TypeDesc { Binary = 1, Clr = t };
            else if (t == typeof(object)) d = new TypeDesc { Binary = 2, Clr = t };
            else if (t == typeof(string[])) d = new TypeDesc { Binary = 6, Clr = t };
            else if (t == typeof(object[])) d = new TypeDesc { Binary = 5, Clr = t };
            else if (TryGetPrimitiveCode(t, out byte pc)) d = new TypeDesc { Binary = 0, Prim = pc, Clr = t };
            else if (t.IsArray && t.GetArrayRank() == 1 && TryGetPrimitiveCode(t.GetElementType()!, out byte ec))
                d = new TypeDesc { Binary = 7, Prim = ec, Clr = t };
            else if (t.IsArray)
            {
                Type el = t;
                while (el.IsArray) el = el.GetElementType()!;
                bool core = IsCoreLib(el.Assembly);
                d = new TypeDesc
                {
                    Binary = core ? (byte)3 : (byte)4,
                    Name = RawName(t),
                    Assembly = core ? MsCorlibName : ResolveName(el).Assembly,
                    Clr = t
                };
            }
            else
            {
                NameInfo n = ResolveName(t);
                d = new TypeDesc
                {
                    Binary = n.IsSystem ? (byte)3 : (byte)4,
                    Name = n.TypeName,
                    Assembly = n.Assembly,
                    Clr = t
                };
            }

            _descCache[t] = d;
            return d;
        }

        private Shape GetShape(Type t)
        {
            if (_shapeCache.TryGetValue(t, out Shape? shape)) return shape;

            NameInfo n = ResolveName(t);

            if (t.IsEnum)
            {
                TryGetPrimitiveCode(Enum.GetUnderlyingType(t), out byte code);
                shape = new Shape(n.TypeName, n.Assembly, n.IsSystem,
                    new[] { "value__" }, new[] { PrimDesc(code) }, null)
                { IsEnum = true };
            }
            else
            {
                var fields = new List<FieldInfo>();
                var names = new List<string>();

                foreach (var (field, serName) in GetSerializableFields(t))
                {
                    if (field.GetCustomAttribute<BfIgnoreAttribute>() != null) continue;
                    var propAttr = field.GetCustomAttribute<BfPropertyAttribute>();
                    fields.Add(field);
                    names.Add(propAttr != null ? propAttr.Name : serName);
                }

                Func<object, object?[]> compiledGetter = CreateFieldAccessor(t, fields);

                shape = new Shape(n.TypeName, n.Assembly, n.IsSystem,
                    names.ToArray(),
                    fields.Select(f => Describe(f.FieldType)).ToArray(),
                    compiledGetter);
            }

            _shapeCache[t] = shape;
            return shape;
        }

        private static Func<object, object?[]> CreateFieldAccessor(Type type, List<FieldInfo> fields)
        {
            if (fields.Count == 0) return static _ => Array.Empty<object?>();

            var param = Expression.Parameter(typeof(object), "obj");
            UnaryExpression typedParam = type.IsValueType
                ? Expression.Convert(param, type)
                : Expression.TypeAs(param, type);

            var arrayElements = fields.Select(field =>
            {
                MemberExpression fieldExpr = Expression.Field(typedParam, field);
                return (Expression)Expression.Convert(fieldExpr, typeof(object));
            });

            var newArray = Expression.NewArrayInit(typeof(object), arrayElements);
            return Expression.Lambda<Func<object, object?[]>>(newArray, param).Compile();
        }

        private static List<(FieldInfo Field, string Name)> GetSerializableFields(Type type)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var result = new List<(FieldInfo, string)>();

            var parents = new List<Type>();
            for (Type? p = type.BaseType; p != null && p != typeof(object) && p != typeof(ValueType) && p != typeof(Enum); p = p.BaseType)
                parents.Add(p);

            bool unique = parents.Select(p => p.Name).Distinct().Count() == parents.Count;
            foreach (Type p in parents)
            {
                foreach (FieldInfo f in p.GetFields(inst | BindingFlags.DeclaredOnly))
                {
                    if (!f.IsPrivate || Attribute.IsDefined(f, typeof(NonSerializedAttribute))) continue;
                    result.Add((f, (unique ? p.Name : p.FullName ?? p.Name) + "+" + f.Name));
                }
            }

            foreach (FieldInfo f in type.GetFields(inst | BindingFlags.DeclaredOnly))
            {
                if (Attribute.IsDefined(f, typeof(NonSerializedAttribute))) continue;
                result.Add((f, f.Name));
            }

            return result;
        }

        private static bool TryGetPrimitiveCode(Type type, out byte code)
        {
            if (type == typeof(bool)) { code = 1; return true; }
            if (type == typeof(byte)) { code = 2; return true; }
            if (type == typeof(char)) { code = 3; return true; }
            if (type == typeof(decimal)) { code = 5; return true; }
            if (type == typeof(double)) { code = 6; return true; }
            if (type == typeof(short)) { code = 7; return true; }
            if (type == typeof(int)) { code = 8; return true; }
            if (type == typeof(long)) { code = 9; return true; }
            if (type == typeof(sbyte)) { code = 10; return true; }
            if (type == typeof(float)) { code = 11; return true; }
            if (type == typeof(TimeSpan)) { code = 12; return true; }
            if (type == typeof(DateTime)) { code = 13; return true; }
            if (type == typeof(ushort)) { code = 14; return true; }
            if (type == typeof(uint)) { code = 15; return true; }
            if (type == typeof(ulong)) { code = 16; return true; }

            code = 0;
            return false;
        }

        private void WritePrimitive(byte code, object val)
        {
            switch (code)
            {
                case 1: Write(Convert.ToBoolean(val)); break;
                case 2: Write(Convert.ToByte(val)); break;
                case 3: Write(Convert.ToChar(val)); break;
                case 5: Write(Convert.ToDecimal(val).ToString(CultureInfo.InvariantCulture)); break;
                case 6: Write(Convert.ToDouble(val)); break;
                case 7: Write(Convert.ToInt16(val)); break;
                case 8: Write(Convert.ToInt32(val)); break;
                case 9: Write(Convert.ToInt64(val)); break;
                case 10: Write(Convert.ToSByte(val)); break;
                case 11: Write(Convert.ToSingle(val)); break;
                case 12: Write(((TimeSpan)val).Ticks); break;
                case 13:
                    {
                        DateTime dt = (DateTime)val;
                        unchecked { Write((long)((ulong)dt.Ticks | ((ulong)(int)dt.Kind << 62))); }
                        break;
                    }
                case 14: Write(Convert.ToUInt16(val)); break;
                case 15: Write(Convert.ToUInt32(val)); break;
                case 16: Write(Convert.ToUInt64(val)); break;
                default: throw new InvalidDataException($"Unknown primitive code {code}");
            }
        }
    }
}