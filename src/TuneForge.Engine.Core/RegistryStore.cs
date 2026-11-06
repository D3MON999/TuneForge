using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace TuneForge.Engine.Core;

/// <summary>A registry value in a form that survives being saved and restored exactly.</summary>
public sealed record RegistryValue(string Kind, string Data)
{
    /// <summary>Number kinds compare by value, so "0" and "00" are treated as equal.</summary>
    public bool SameAs(RegistryValue? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            "dword" or "qword" => ulong.Parse(Data, CultureInfo.InvariantCulture) == ulong.Parse(other.Data, CultureInfo.InvariantCulture),
            "binary" => string.Equals(Data, other.Data, StringComparison.OrdinalIgnoreCase),
            _ => Data == other.Data,
        };
    }
}

public interface IRegistryStore
{
    bool KeyExists(string hive, string path);

    RegistryValue? Read(string hive, string path, string name);

    IReadOnlyList<string> ValueNames(string hive, string path);

    void Write(string hive, string path, string name, RegistryValue value);

    void DeleteValue(string hive, string path, string name);

    /// <summary>Removes a key only if it holds nothing, so undo can't delete real data.</summary>
    void DeleteKeyIfEmpty(string hive, string path);
}

/// <summary>Reads and writes the real registry. HKCU always means the signed-in user.</summary>
public sealed class WindowsRegistryStore(SecurityIdentifier user) : IRegistryStore
{
    public bool KeyExists(string hive, string path)
    {
        using var root = OpenRoot(hive, out var prefix);
        using var key = root.OpenSubKey(prefix + path);
        return key is not null;
    }

    public RegistryValue? Read(string hive, string path, string name)
    {
        using var root = OpenRoot(hive, out var prefix);
        using var key = root.OpenSubKey(prefix + path);
        if (key is null || key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return null;
        }

        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistryValue("dword", unchecked((uint)(int)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => new RegistryValue("qword", unchecked((ulong)(long)value).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.String => new RegistryValue("string", (string)value),
            RegistryValueKind.ExpandString => new RegistryValue("expand", (string)value),
            RegistryValueKind.MultiString => new RegistryValue("multi", string.Join('\0', (string[])value)),
            RegistryValueKind.Binary => new RegistryValue("binary", Convert.ToHexString((byte[])value)),
            _ => throw new NotSupportedException($"Unsupported registry type for {name}."),
        };
    }

    public IReadOnlyList<string> ValueNames(string hive, string path)
    {
        using var root = OpenRoot(hive, out var prefix);
        using var key = root.OpenSubKey(prefix + path);
        return key?.GetValueNames() ?? [];
    }

    public void Write(string hive, string path, string name, RegistryValue value)
    {
        using var root = OpenRoot(hive, out var prefix);
        using var key = root.CreateSubKey(prefix + path, writable: true);
        switch (value.Kind)
        {
            case "dword":
                key.SetValue(name, unchecked((int)uint.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                break;
            case "qword":
                key.SetValue(name, unchecked((long)ulong.Parse(value.Data, CultureInfo.InvariantCulture)), RegistryValueKind.QWord);
                break;
            case "string":
                key.SetValue(name, value.Data, RegistryValueKind.String);
                break;
            case "expand":
                key.SetValue(name, value.Data, RegistryValueKind.ExpandString);
                break;
            case "multi":
                key.SetValue(name, value.Data.Length == 0 ? [] : value.Data.Split('\0'), RegistryValueKind.MultiString);
                break;
            case "binary":
                key.SetValue(name, Convert.FromHexString(value.Data), RegistryValueKind.Binary);
                break;
            default:
                throw new NotSupportedException($"Unsupported registry type: {value.Kind}");
        }
    }

    public void DeleteValue(string hive, string path, string name)
    {
        using var root = OpenRoot(hive, out var prefix);
        using var key = root.OpenSubKey(prefix + path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyIfEmpty(string hive, string path)
    {
        using var root = OpenRoot(hive, out var prefix);
        using (var key = root.OpenSubKey(prefix + path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }

        root.DeleteSubKey(prefix + path, throwOnMissingSubKey: false);
    }

    private RegistryKey OpenRoot(string hive, out string prefix)
    {
        switch (hive)
        {
            case "HKLM":
                prefix = string.Empty;
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            case "HKCU":
                prefix = user.Value + @"\";
                return RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            default:
                throw new NotSupportedException($"Unsupported hive: {hive}");
        }
    }
}
