using System;
using YHG.FastBF.Binary;

namespace YHG.FastBF
{
    public interface IBfReadable
    {
        void ReadBf(NrbfClass record);
    }

    public interface IBfSerializable : IBfReadable
    {
    }

    public interface IBfDeserialized
    {
        void OnDeserialized();
    }
}