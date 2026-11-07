# YHG.FastBF

A high-performance, zero-dependency [MS-NRBF] (BinaryFormatter) serialization and deserialization engine for modern .NET.

[![NuGet Version](https://img.shields.io/nuget/v/YHG.FastBF.svg)](https://www.nuget.org/packages/YHG.FastBF)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET Version](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Supported-brightgreen.svg)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

---

## Overview

Starting with .NET 8 and .NET 9, Microsoft marked `BinaryFormatter` obsolete and completely disabled it by default due to critical remote code execution (RCE) attack vectors. 

**YHG.FastBF** provides a secure, standalone replacement designed to read and write the official Microsoft .NET Remoting Binary Format (`[MS-NRBF]`) specification. It allows modern applications to serialize and hydrate binary streams, Unity asset structures, and legacy save containers without relying on deprecated framework APIs.

---

## Features

- **Protocol Compliant:** Fully implements major wire tags of the `[MS-NRBF]` specification.
- **Native AOT & Trimming Ready:** Fully annotated with `[DynamicallyAccessedMembers]` to preserve metadata during IL trimming; executes reliably under Native AOT environments.
- **High Throughput:** Dynamic compiled `Expression` trees replace slow runtime reflection with fast member extractors, utilizing internal interpreted execution fallback on Native AOT targets.
- **Cross-Framework Interoperability:** Automatically aliases modern `System.Private.CoreLib` types to legacy `mscorlib, Version=4.0.0.0` for backward compatibility with Unity and .NET Framework.
- **Graph Resolution:** Multi-pass object tracking resolves circular references and shared object instances safely.
- **Dictionary & Collection Hydration:** Native support for generic dictionaries, key-value pair arrays, and primitive lists.
- **Zero External Dependencies:** Pure managed code running seamlessly across `AnyCPU` (x86, x64, ARM64).

---

## Installation

### .NET CLI
```bash
dotnet add package YHG.FastBF
```

### Package Manager
```powershell
Install-Package YHG.FastBF
```

---

## Quick Start

### 1. Model Definition

You can map legacy or custom property names using attributes:

```csharp
using System.Collections.Generic;
using YHG.FastBF.Attributes;

[BfType("GameData.PlayerProfile", "Assembly-CSharp")]
public class PlayerProfile : IBfDeserialized
{
    [BfProperty("player_level")]
    public int Level { get; set; }

    public string PlayerName { get; set; } = string.Empty;

    public Dictionary<string, int> Inventory { get; set; } = new();

    [BfIgnore]
    public int EphemeralCache { get; set; }

    public void OnDeserialized()
    {
        // Executed immediately after hydration completes
    }
}
```

### 2. Writing (Serialization)

```csharp
using System.IO;
using System.Collections.Generic;
using YHG.FastBF.Binary;

var profile = new PlayerProfile
{
    Level = 50,
    PlayerName = "Commander",
    Inventory = new Dictionary<string, int>
    {
        { "HealthPotion", 10 },
        { "GoldCoins", 2500 }
    }
};

using (var stream = File.Create("savedata.bin"))
using (var writer = new BfWriter(stream))
{
    writer.WriteRootClass(profile);
}
```

### 3. Reading (Deserialization)

```csharp
using System;
using System.IO;
using YHG.FastBF.Binary;

using (var stream = File.OpenRead("savedata.bin"))
using (var reader = new BfReader(stream))
{
    PlayerProfile profile = reader.ReadRootClass<PlayerProfile>();
    Console.WriteLine($"Loaded: {profile.PlayerName} (Level {profile.Level})");
}
```

---

## Supported [MS-NRBF] Records

| Tag | Record Name | Status |
| :---: | :--- | :---: |
| `0x00` | `SerializedStreamHeader` | Supported |
| `0x01` | `ClassWithId` | Supported |
| `0x04` | `SystemClassWithMembers` | Supported |
| `0x05` | `ClassWithMembersAndTypes` | Supported |
| `0x06` | `BinaryObjectString` | Supported |
| `0x07` | `BinaryArray` | Supported |
| `0x08` | `MemberPrimitiveTyped` | Supported |
| `0x09` | `MemberReference` | Supported |
| `0x0A` | `ObjectNull` | Supported |
| `0x0B` | `MessageEnd` | Supported |
| `0x0C` | `BinaryLibrary` | Supported |
| `0x0D` | `ObjectNullMultiple256` | Supported |
| `0x0E` | `ObjectNullMultiple` | Supported |
| `0x0F` | `ArraySinglePrimitive` | Supported |
| `0x10` | `ArraySingleObject` | Supported |
| `0x11` | `ArraySingleString` | Supported |

---

## License

This project is licensed under the [MIT License](LICENSE).