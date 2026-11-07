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