using System;
using System.Collections.Generic;

namespace YHG.FastBF.Binary
{
    public sealed class NrbfClass
    {
        public int ObjectId { get; internal set; }
        public string ClassName { get; internal set; } = string.Empty;
        public string[] MemberNames { get; internal set; } = Array.Empty<string>();
        public object?[] Values { get; internal set; } = Array.Empty<object?>();

        public object? this[string name] => Get(name);

        public int IndexOf(string name)
        {
            for (int i = 0; i < MemberNames.Length; i++)
            {
                if (string.Equals(MemberNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            for (int i = 0; i < MemberNames.Length; i++)
            {
                if (MemberNames[i].EndsWith("+" + name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(MemberNames[i], "<" + name + ">k__BackingField", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        public object? Get(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : Values[i];
        }

        public string GetString(string name, string fallback = "") =>
            Get(name)?.ToString() ?? fallback;

        public int GetInt32(string name, int fallback = 0) =>
            Get(name) != null ? ToInt(Get(name)!) : fallback;

        public long GetInt64(string name, long fallback = 0) =>
            Get(name) is long l ? l : (Get(name) != null ? ToInt(Get(name)!) : fallback);

        public float GetSingle(string name, float fallback = 0f) =>
            Get(name) != null ? ToFloat(Get(name)!) : fallback;

        public double GetDouble(string name, double fallback = 0.0) =>
            Get(name) is double d ? d : (Get(name) != null ? ToFloat(Get(name)!) : fallback);

        public bool GetBoolean(string name, bool fallback = false) =>
            Get(name) != null ? ToBool(Get(name)!) : fallback;

        public T? GetClass<T>(string name) where T : class, IBfReadable, new()
        {
            if (Get(name) is NrbfClass subClass)
            {
                var inst = new T();
                inst.ReadBf(subClass);
                return inst;
            }
            return null;
        }

        public static int ToInt(object? v) => v switch
        {
            int i => i,
            byte b => b,
            short s => s,
            ushort us => us,
            uint ui => (int)ui,
            long l => (int)l,
            bool bo => bo ? 1 : 0,
            float f => (int)f,
            double d => (int)d,
            NrbfClass c when c.IndexOf("value__") >= 0 => ToInt(c.Get("value__")),
            _ => 0
        };

        public static float ToFloat(object? v) => v switch
        {
            float f => f,
            double d => (float)d,
            decimal m => (float)m,
            null => 0f,
            _ => ToInt(v)
        };

        public static bool ToBool(object? v) => v is bool b ? b : ToInt(v) != 0;

        public IEnumerable<(object? Key, object? Value)> GetDictionaryPairs()
        {
            object? rawPairs = Get("KeyValuePairs");
            if (rawPairs is not object?[] arr) yield break;

            foreach (object? item in arr)
            {
                if (item is NrbfClass kv)
                {
                    yield return (kv.Get("key") ?? kv.Get("Key"), kv.Get("value") ?? kv.Get("Value"));
                }
            }
        }
    }
}