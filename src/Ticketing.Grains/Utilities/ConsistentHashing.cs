using System.Security.Cryptography;
using System.Text;

namespace Ticketing.Grains.Utilities;

/// <summary>
/// Implémentation de Jump Consistent Hash (Google).
/// Algorithme optimal pour distribution uniforme avec minimal remapping lors de scale up/down.
///
/// Paper: "A Fast, Minimal Memory, Consistent Hash Algorithm" by John Lamping, Eric Veach (Google)
/// https://arxiv.org/abs/1406.2294
/// </summary>
public static class ConsistentHashing
{
    /// <summary>
    /// Jump Consistent Hash - algorithme de hashing distribué minimal.
    /// Temps de calcul: O(log(numBuckets))
    /// Propriété: Lorsque numBuckets augmente de N à N+1, seulement 1/N+1 des clés sont remappées.
    /// </summary>
    /// <param name="key">Clé à hasher (64-bit)</param>
    /// <param name="numBuckets">Nombre de buckets (silos) disponibles</param>
    /// <returns>Index du bucket [0, numBuckets-1]</returns>
    public static int JumpHash(ulong key, int numBuckets)
    {
        if (numBuckets <= 0)
        {
            throw new ArgumentException("Number of buckets must be positive", nameof(numBuckets));
        }

        long b = -1;
        long j = 0;

        while (j < numBuckets)
        {
            b = j;
            key = key * 2862933555777941757UL + 1;
            j = (long)((b + 1) * (double)(1L << 31) / (double)((key >> 33) + 1));
        }

        return (int)b;
    }

    /// <summary>
    /// Jump Hash pour une clé string (ex: userId).
    /// Convertit la string en ulong via hashing.
    /// </summary>
    /// <param name="key">Clé string (ex: userId, grainId)</param>
    /// <param name="numBuckets">Nombre de buckets (silos) disponibles</param>
    /// <returns>Index du bucket [0, numBuckets-1]</returns>
    public static int JumpHash(string key, int numBuckets)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        }

        var hash = ComputeHash(key);
        return JumpHash(hash, numBuckets);
    }

    /// <summary>
    /// Calcule le hash 64-bit d'une string (via xxHash ou FNV1a pour performance).
    /// Utilise SHA256 pour simplicité (peut être remplacé par xxHash64 pour perf ultime).
    /// </summary>
    private static ulong ComputeHash(string key)
    {
        // Option 1: SHA256 (plus lent mais disponible nativement)
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));

        // Prendre les 8 premiers bytes pour obtenir un ulong
        return BitConverter.ToUInt64(hashBytes, 0);

        // Option 2 (plus rapide): FNV-1a 64-bit
        // return FNV1a64(key);
    }

    /// <summary>
    /// FNV-1a hash 64-bit (alternative ultra-rapide pour production).
    /// </summary>
    public static ulong FNV1a64(string key)
    {
        const ulong FnvPrime = 0x100000001b3;
        const ulong FnvOffsetBasis = 0xcbf29ce484222325;

        var hash = FnvOffsetBasis;
        var bytes = Encoding.UTF8.GetBytes(key);

        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// Calcule le placement d'une clé sur un ensemble de buckets avec virtual nodes.
    /// Virtual nodes améliorent la distribution uniforme.
    /// </summary>
    /// <param name="key">Clé à placer</param>
    /// <param name="numBuckets">Nombre de buckets physiques</param>
    /// <param name="vnodeMultiplier">Nombre de virtual nodes par bucket (ex: 150)</param>
    /// <returns>Index du bucket physique [0, numBuckets-1]</returns>
    public static int JumpHashWithVirtualNodes(string key, int numBuckets, int vnodeMultiplier = 150)
    {
        var totalVnodes = numBuckets * vnodeMultiplier;
        var hash = ComputeHash(key);
        var vnodeIndex = JumpHash(hash, totalVnodes);

        // Mapper le vnode vers le bucket physique
        return vnodeIndex / vnodeMultiplier;
    }

    /// <summary>
    /// Renvoie une liste de buckets candidats (avec fallback) pour haute disponibilité.
    /// Utile pour répliquer sur plusieurs silos.
    /// </summary>
    /// <param name="key">Clé à placer</param>
    /// <param name="numBuckets">Nombre de buckets</param>
    /// <param name="replicaCount">Nombre de réplicas (ex: 3)</param>
    /// <returns>Liste d'indices de buckets ordonnés par préférence</returns>
    public static List<int> GetReplicaBuckets(string key, int numBuckets, int replicaCount)
    {
        if (replicaCount > numBuckets)
        {
            throw new ArgumentException("Replica count cannot exceed number of buckets");
        }

        var buckets = new HashSet<int>();
        var hash = ComputeHash(key);

        // Stratégie: on génère des clés dérivées pour chaque réplica
        for (int i = 0; i < replicaCount * 10 && buckets.Count < replicaCount; i++)
        {
            var derivedHash = hash + (ulong)i;
            var bucket = JumpHash(derivedHash, numBuckets);
            buckets.Add(bucket);
        }

        return buckets.ToList();
    }

    /// <summary>
    /// Teste la distribution du hashing sur N buckets avec M clés.
    /// Retourne les statistiques de distribution (pour validation).
    /// </summary>
    public static Dictionary<int, int> TestDistribution(int numKeys, int numBuckets, Func<int, string> keyGenerator)
    {
        var distribution = new Dictionary<int, int>();
        for (int i = 0; i < numBuckets; i++)
        {
            distribution[i] = 0;
        }

        for (int i = 0; i < numKeys; i++)
        {
            var key = keyGenerator(i);
            var bucket = JumpHash(key, numBuckets);
            distribution[bucket]++;
        }

        return distribution;
    }
}

/// <summary>
/// Custom placement strategy pour Orleans utilisant Jump Consistent Hash.
/// Alternative: peut être intégré comme IPlacementDirector custom.
/// </summary>
public class JumpHashPlacementStrategy
{
    private readonly int _siloCount;

    public JumpHashPlacementStrategy(int siloCount)
    {
        _siloCount = siloCount;
    }

    /// <summary>
    /// Détermine le silo cible pour un grainId donné.
    /// </summary>
    public int GetTargetSilo(string grainId)
    {
        return ConsistentHashing.JumpHash(grainId, _siloCount);
    }

    /// <summary>
    /// Met à jour le nombre de silos (lors d'un scaling).
    /// Note: Orleans gère automatiquement le remapping des grains.
    /// </summary>
    public JumpHashPlacementStrategy WithSiloCount(int newSiloCount)
    {
        return new JumpHashPlacementStrategy(newSiloCount);
    }
}
