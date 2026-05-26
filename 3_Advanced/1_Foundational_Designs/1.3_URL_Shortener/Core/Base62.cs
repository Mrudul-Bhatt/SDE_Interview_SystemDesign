// Base62 Encoder / Decoder
// Maps a numeric ID to a short alphanumeric string and back.
// Alphabet: 0-9, a-z, A-Z (62 characters)
// 7-char fixed width → 62^7 ≈ 3.5 trillion unique codes.

namespace AdvancedDesigns
{
    public static class Base62
    {
        private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const int Base = 62;

        // Fixed 7 chars so all codes have the same length in the short URL.
        // 62^7 ≈ 3.5 trillion — enough for ~96 years at 100M new URLs per day.
        private const int Length = 7;

        public static string Encode(long id)
        {
            // Fill the array right-to-left: each iteration extracts the least
            // significant digit in base-62, then shifts id right by dividing.
            // Starting from the right ensures the most-significant digit is at index 0.
            var chars = new char[Length];
            for (int i = Length - 1; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(id % Base)];
                id /= Base;
            }
            return new string(chars);
        }

        public static long Decode(string code)
        {
            // Mirror of Encode: accumulate left-to-right, multiplying by base each step.
            // Same logic as parsing a decimal string — just base 62 instead of 10.
            long result = 0;
            foreach (char c in code)
                result = result * Base + Alphabet.IndexOf(c);
            return result;
        }
    }
}
