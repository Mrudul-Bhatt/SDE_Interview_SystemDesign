// RankedCompletion — a single autocomplete suggestion with its search frequency.
// Frequency drives ranking: higher = appears earlier in the suggestion list.

namespace AdvancedDesigns
{
    public class RankedCompletion
    {
        public string Term { get; set; }
        public int Frequency { get; set; }

        // Readable format used when printing results in the demo.
        public override string ToString() => $"\"{Term}\" (freq={Frequency:N0})";
    }
}
