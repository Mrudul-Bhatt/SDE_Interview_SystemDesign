// Similarity — vector math used everywhere in the system.
//
// Two cosine variants because we have two representations:
//   - Sparse Dictionary (user_id → rating) for item-item CF similarities, where
//     most entries are zero and iterating only over non-zero values is much
//     faster than a dense scan.
//   - Dense double[] for learned embeddings (matrix factorization, two-tower),
//     where the vectors are short (k=8–256) and fully populated.
//
// Dot is used for matrix factorization prediction where we add explicit biases
// separately — the dot product alone is the latent interaction term.

using System;
using System.Collections.Generic;

public static class Similarity
{
    // Cosine similarity between two sparse rating vectors (item-item or user-user)
    public static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        double dot = 0, normA = 0, normB = 0;
        foreach (var kv in a)
        {
            normA += kv.Value * kv.Value;
            if (b.TryGetValue(kv.Key, out var bv)) dot += kv.Value * bv;
        }
        foreach (var kv in b) normB += kv.Value * kv.Value;
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    // Cosine on dense float arrays (for embedding vectors)
    public static double Cosine(double[] a, double[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    // Dot product (for MF prediction, embeddings already normalized during training)
    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
