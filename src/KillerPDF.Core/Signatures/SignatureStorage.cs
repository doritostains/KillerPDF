using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPDF;

/// <summary>
/// Persists the user's saved signatures to a JSON file in a UI-supplied directory.
/// WPF/Avalonia each call with AppDomain.CurrentDomain.BaseDirectory (i.e. next to the .exe)
/// so the portable build keeps signatures co-located with the app.
/// </summary>
public static class SignatureStorage
{
    public const string FileName = "signatures.json";

    public static List<SavedSignature> Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return new List<SavedSignature>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, SignatureJsonContext.Default.ListSavedSignature)
                   ?? new List<SavedSignature>();
        }
        catch
        {
            return new List<SavedSignature>();
        }
    }

    public static void Save(string directory, List<SavedSignature> signatures)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var json = JsonSerializer.Serialize(signatures, SignatureJsonContext.Default.ListSavedSignature);
            File.WriteAllText(path, json);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>
/// Source-generated JSON serialization context so signature load/save is trim/AOT-safe.
/// </summary>
[JsonSerializable(typeof(List<SavedSignature>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SignatureJsonContext : JsonSerializerContext { }
