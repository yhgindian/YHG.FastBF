namespace YHG.FastBF.Binary
{
    public delegate void NrbfBindToTypeBinderDelegate(ref string? assemblyName, ref string typeName);
    public delegate void NrbfBindToNameBinderDelegate(Type serializedType, ref string? assemblyName, ref string typeName);
}