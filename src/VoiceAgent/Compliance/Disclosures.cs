namespace VoiceAgent.Compliance;

/// <summary>
/// Wording that must be said exactly is not left to a speech-to-speech model, which paraphrases.
/// Each disclosure is a text file (the wording, owned by compliance) and the same wording
/// pre-rendered to 8 kHz mu-law audio; the bridge plays the audio itself and holds the model
/// until Twilio confirms, with a mark, that the clip has finished playing.
///
/// data/disclosures/{name}.txt   the exact wording
/// data/disclosures/{name}.ulaw  that wording, rendered by tools/make_disclosures.py
/// </summary>
public sealed class DisclosureLibrary
{
    public const string RecordingNotice = "recording-notice";
    public const string ServicingNotice = "servicing-notice";

    private readonly Dictionary<string, (string Text, byte[] Audio)> _items = new(StringComparer.Ordinal);

    public DisclosureLibrary(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var audioPath in Directory.GetFiles(directory, "*.ulaw"))
        {
            string name = Path.GetFileNameWithoutExtension(audioPath);
            string textPath = Path.ChangeExtension(audioPath, ".txt");
            if (File.Exists(textPath))
                _items[name] = (File.ReadAllText(textPath).Trim(), File.ReadAllBytes(audioPath));
        }
    }

    public bool TryGet(string name, out string text, out byte[] audio)
    {
        if (_items.TryGetValue(name, out var item))
        {
            (text, audio) = item;
            return true;
        }
        (text, audio) = ("", []);
        return false;
    }

    public IReadOnlyCollection<string> Names => _items.Keys;
}
