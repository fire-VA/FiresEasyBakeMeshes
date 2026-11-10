namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The impostor atlases' disk store: [generator]'s FarCache (#4), Global scope (atlases do not depend on the world). One place to
    /// change if the store moves. FarCache's own [Far Cache] Enabled gates it (off = every bake is fresh, nothing written).
    /// </summary>
    internal static class ImpostorDiskCache
    {
        internal static bool TryRead(string bucket, string key, out byte[] data) => FarCache.TryRead(FarCacheScope.Global, bucket, key, out data);
        internal static void Write(string bucket, string key, byte[] data) => FarCache.Write(FarCacheScope.Global, bucket, key, data);
        internal static string Key(params object[] parts) => FarCache.Key(parts);
    }
}
