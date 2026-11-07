using System;

namespace YHG.FastBF.Attributes
{
    /// <summary>
    /// Explicitly maps a class field or property to a member name in the binary stream.
    /// Equivalent to [JsonProperty("name")] in JSON serializers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class BfPropertyAttribute : Attribute
    {
        public string Name { get; }

        public BfPropertyAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }

    /// <summary>
    /// Excludes the marked field or property from both reading and writing operations.
    /// Useful for runtime-only state, UI bindings, and calculated fields.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class BfIgnoreAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies the target Type name and Assembly identity used during binary serialization.
    /// Crucial for Unity/Mono payloads that expect "Assembly-CSharp" or "CustomDictionary`2".
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BfTypeAttribute : Attribute
    {
        public string? TypeName { get; }
        public string? AssemblyName { get; }

        public BfTypeAttribute(string? typeName = null, string? assemblyName = null)
        {
            TypeName = typeName;
            AssemblyName = assemblyName;
        }
    }
}