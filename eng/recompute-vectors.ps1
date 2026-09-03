[CmdletBinding()]
param(
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public sealed record IndependentVector(string Name, string Hex, string Sha256);

public static class IndependentVectorRecomputation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IndependentVector[] Compute()
    {
        return new[]
        {
            Source("empty-source-set", Array.Empty<(string Hint, string Text)>()),
            Source("source-x", new[] { ("A.g.cs", "x") }),
            Source("source-composed-e-acute", new[] { ("A.g.cs", "\u00e9") }),
            Source("source-decomposed-e-acute", new[] { ("A.g.cs", "e\u0301") }),
            Source("source-literal-feff", new[] { ("A.g.cs", "\ufeff") }),
            DiagnosticEmpty(),
            DiagnosticDuplicateObservableEmptyHelpLink(),
            Raw("location-none", Frame(Utf8("none"))),
            Raw("location-source-visible-unmapped", SourceLocation(mappedState: 0, mappedPathKind: 0, mappedPath: null)),
            Raw("location-source-visible-mapped-empty", SourceLocation(mappedState: 1, mappedPathKind: 3, mappedPath: string.Empty)),
            Raw(
                "location-source-visible-mapped-nonempty",
                SourceLocation(
                    mappedState: 1,
                    mappedPathKind: 2,
                    mappedPath: "external:0000000000000000000000000000000000000000000000000000000000000000"))
        };
    }

    private static IndependentVector Source(string name, IEnumerable<(string Hint, string Text)> values)
    {
        byte[][] records = values
            .OrderBy(value => value.Hint, StringComparer.Ordinal)
            .Select(value => Concat(
                Frame(Utf8("sga-source-v1")),
                Frame(Utf8(value.Hint)),
                Frame(Utf8(value.Text))))
            .ToArray();

        List<byte[]> parts = new()
        {
            Frame(Utf8("sga-source-set-v1")),
            U64((ulong)records.Length)
        };
        parts.AddRange(records.Select(Frame));
        return Raw(name, Concat(parts.ToArray()));
    }

    private static IndependentVector DiagnosticEmpty() => Raw(
        "empty-diagnostic-set",
        Concat(Frame(Utf8("sga-diagnostic-set-v1")), U64(0)));

    private static IndependentVector DiagnosticDuplicateObservableEmptyHelpLink()
    {
        byte[] record = Concat(
            Frame(Utf8("sga-diagnostic-v1")),
            Str("SGA001"),
            Str("Warning"),
            Bool(false),
            Bool(false),
            U64(1),
            Str("caf\u00e9"),
            Str("Test"),
            Str("Warning"),
            Str(string.Empty),
            Seq(new[] { Str("tag") }),
            Frame(Frame(Utf8("none"))),
            Seq(Array.Empty<byte[]>()),
            Seq(new[] { Concat(Str("k"), Str(null)) }));

        return Raw(
            "duplicate-diagnostic-observable-empty-help-link",
            Concat(
                Frame(Utf8("sga-diagnostic-set-v1")),
                U64(1),
                Frame(record),
                U64(2)));
    }

    private static byte[] SourceLocation(byte mappedState, byte mappedPathKind, string? mappedPath)
    {
        List<byte[]> parts = new()
        {
            Frame(Utf8("source")),
            PathValue(0, "Input.cs"),
            U64(0),
            U64(1),
            new[] { mappedState }
        };

        if (mappedState == 1)
        {
            parts.Add(PathValue(mappedPathKind, mappedPath!));
        }

        parts.Add(U64(0));
        parts.Add(U64(0));
        parts.Add(U64(0));
        parts.Add(U64(1));
        parts.Add(new byte[] { 0 });
        return Concat(parts.ToArray());
    }

    private static byte[] PathValue(byte kind, string value) => Concat(Str(value), new[] { kind });

    private static IndependentVector Raw(string name, byte[] bytes) => new(
        name,
        Convert.ToHexStringLower(bytes),
        Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static byte[] Utf8(string value) => StrictUtf8.GetBytes(value);

    private static byte[] Bool(bool value) => new[] { value ? (byte)1 : (byte)0 };

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Frame(byte[] value) => Concat(U64((ulong)value.Length), value);

    private static byte[] Str(string? value) => value is null
        ? new byte[] { 0 }
        : Concat(new byte[] { 1 }, Frame(Utf8(value)));

    private static byte[] Seq(IEnumerable<byte[]> values)
    {
        byte[][] materialized = values.ToArray();
        List<byte[]> parts = new() { U64((ulong)materialized.Length) };
        parts.AddRange(materialized.Select(Frame));
        return Concat(parts.ToArray());
    }

    private static byte[] Concat(params byte[][] values)
    {
        int length = checked(values.Sum(value => value.Length));
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] value in values)
        {
            Buffer.BlockCopy(value, 0, result, offset, value.Length);
            offset += value.Length;
        }

        return result;
    }
}
'@

$expected = @{
    'empty-source-set' = @(
        '00000000000000117367612d736f757263652d7365742d76310000000000000000',
        'b28b860f8b7a846ce6115b4913d6c6fdb3370869f2664152d9917f0362ad1586')
    'source-x' = @(
        '00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002c000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000178',
        '5fd0ef7f721b43a4fb3a89f2519cd908afd58796b8354e8e28ba366ab20ff744')
    'source-composed-e-acute' = @(
        '00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002d000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000002c3a9',
        '34350d1fddc2c27512c246e8bf8bec5ec25afd6db7bc081116bcfe24306b3773')
    'source-decomposed-e-acute' = @(
        '00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e6373000000000000000365cc81',
        '06a411fa1b78d672510fccdad4932912cfac5005351a59d9ad7db01c66bf8c40')
    'source-literal-feff' = @(
        '00000000000000117367612d736f757263652d7365742d76310000000000000001000000000000002e000000000000000d7367612d736f757263652d76310000000000000006412e672e63730000000000000003efbbbf',
        '331c7aa9b0219298cf01c15b05e5b398fca88fac1d0e42725889610588587006')
    'empty-diagnostic-set' = @(
        '00000000000000157367612d646961676e6f737469632d7365742d76310000000000000000',
        '319425da831882c5bbfdcabedb1b577646d92833db95987f57c1c852b574b048')
    'duplicate-diagnostic-observable-empty-help-link' = @(
        '00000000000000157367612d646961676e6f737469632d7365742d7631000000000000000100000000000000c900000000000000117367612d646961676e6f737469632d76310100000000000000065347413030310100000000000000075761726e696e6700000000000000000001010000000000000005636166c3a9010000000000000004546573740100000000000000075761726e696e670100000000000000000000000000000001000000000000000c010000000000000003746167000000000000000c00000000000000046e6f6e6500000000000000000000000000000001000000000000000b0100000000000000016b000000000000000002',
        'b2bbc1538dc65a17d1d7422f7d43e28bf739ad0afe1978b8db455c95fb1f0bb2')
    'location-none' = @(
        '00000000000000046e6f6e65',
        '9506f170afb36fca8c02831b18c88d7247935cae33b932f00eed9c9263e3ab6c')
    'location-source-visible-unmapped' = @(
        '0000000000000006736f75726365010000000000000008496e7075742e6373000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000100',
        'ad9845e844ed5f8900b1071e5d918ffd66f1391cb7329c9fc6115bde2dbf5870')
    'location-source-visible-mapped-empty' = @(
        '0000000000000006736f75726365010000000000000008496e7075742e637300000000000000000000000000000000010101000000000000000003000000000000000000000000000000000000000000000000000000000000000100',
        'b044182f465972d2341d849d9a5162f25993489aabd94d26b034aba50ddbd278')
    'location-source-visible-mapped-nonempty' = @(
        '0000000000000006736f75726365010000000000000008496e7075742e637300000000000000000000000000000000010101000000000000004965787465726e616c3a3030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303030303002000000000000000000000000000000000000000000000000000000000000000100',
        '356c04407696fcd6a62d459d14743fbd1e60bd64a0e1dfb7f2b76b31c540bd8a')
}

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($vector in [IndependentVectorRecomputation]::Compute()) {
    $pair = $expected[$vector.Name]
    if ($null -eq $pair) {
        throw "No expected value is declared for $($vector.Name)."
    }

    if ($vector.Hex -cne $pair[0] -or $vector.Sha256 -cne $pair[1]) {
        throw "Independent recomputation mismatch for $($vector.Name): actual hash=$($vector.Sha256) hex=$($vector.Hex)"
    }

    $lines.Add("$($vector.Name) bytes=$([Convert]::FromHexString($vector.Hex).Length) sha256=$($vector.Sha256) hex=$($vector.Hex)")
}

$output = $lines -join [Environment]::NewLine
Write-Output $output

if ($EvidencePath) {
    $absoluteEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    $parent = Split-Path -Parent $absoluteEvidencePath
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllText(
        $absoluteEvidencePath,
        $output + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}
