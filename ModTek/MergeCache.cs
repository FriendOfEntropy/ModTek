using System;
using System.Collections.Generic;
using System.IO;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModTek
{
    using static ModTek;
    using static Logger;

    internal class MergeCache
    {
        [UsedImplicitly]
        public Dictionary<string, CacheEntry> CachedEntries { get; set; } = new Dictionary<string, CacheEntry>();

        /// <summary>
        ///     Gets (from the cache) or creates (and adds to cache) a JSON merge
        /// </summary>
        /// <param name="absolutePath">The path to the original JSON file</param>
        /// <param name="mergePaths">A list of the paths to merged in JSON</param>
        /// <returns>A path to the cached JSON that contains the original JSON with the mod merges applied</returns>
        public string GetOrCreateCachedEntry(string absolutePath, List<string> mergePaths)
        {
            absolutePath = Path.GetFullPath(absolutePath);
            var relativePath = GetRelativePath(absolutePath, GameDirectory);

            Log("");

            if (!CachedEntries.ContainsKey(relativePath) || !CachedEntries[relativePath].MatchesPaths(absolutePath, mergePaths))
            {
                var cachedAbsolutePath = Path.GetFullPath(Path.Combine(CacheDirectory, relativePath));
                var cachedEntry = new CacheEntry(cachedAbsolutePath, absolutePath, mergePaths);

                if (cachedEntry.HasErrors)
                    return null;

                CachedEntries[relativePath] = cachedEntry;

                Log($"Merge performed: {Path.GetFileName(absolutePath)}");
            }
            else
            {
                Log($"Cached merge: {Path.GetFileName(absolutePath)} ({File.GetLastWriteTime(CachedEntries[relativePath].CacheAbsolutePath):G})");
            }

            Log($"\t{relativePath}");

            foreach (var contributingPath in mergePaths)
                Log($"\t{GetRelativePath(contributingPath, ModsDirectory)}");

            Log("");

            CachedEntries[relativePath].CacheHit = true;
            return CachedEntries[relativePath].CacheAbsolutePath;
        }

        public bool HasCachedEntry(string originalPath, List<string> mergePaths)
        {
            var relativePath = GetRelativePath(originalPath, GameDirectory);
            return CachedEntries.ContainsKey(relativePath) && CachedEntries[relativePath].MatchesPaths(originalPath, mergePaths);
        }

        /// <summary>
        ///     Writes the cache to disk to the path, after cleaning up old entries
        /// </summary>
        /// <param name="path">Where the cache should be written to</param>
        public void WriteCacheToDisk(string path)
        {
            // remove all of the cache that we didn't use
            var unusedMergePaths = new List<string>();
            foreach (var cachedEntryKVP in CachedEntries)
                if (!cachedEntryKVP.Value.CacheHit)
                    unusedMergePaths.Add(cachedEntryKVP.Key);

            if (unusedMergePaths.Count > 0)
                Log("");

            foreach (var unusedMergePath in unusedMergePaths)
            {
                var cacheAbsolutePath = CachedEntries[unusedMergePath].CacheAbsolutePath;
                CachedEntries.Remove(unusedMergePath);

                if (File.Exists(cacheAbsolutePath))
                    File.Delete(cacheAbsolutePath);

                Log($"Old Merge Deleted: {cacheAbsolutePath}");

                var directory = Path.GetDirectoryName(cacheAbsolutePath);
                while (Directory.Exists(directory) && Directory.GetDirectories(directory).Length == 0 && Directory.GetFiles(directory).Length == 0 && Path.GetFullPath(directory) != CacheDirectory)
                {
                    Directory.Delete(directory);
                    Log($"Old Merge folder deleted: {directory}");
                    directory = Path.GetFullPath(Path.Combine(directory, ".."));
                }
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>
        /// Updates all absolute path'd cache entries to use a relative path instead
        /// </summary>
        public void UpdateToRelativePaths()
        {
            var toRemove = new List<string>();
            var toAdd = new Dictionary<string, CacheEntry>();

            foreach (var path in CachedEntries.Keys)
            {
                if (Path.IsPathRooted(path))
                {
                    var relativePath = GetRelativePath(path, GameDirectory);

                    toAdd[relativePath] = CachedEntries[path];
                    toRemove.Add(path);

                    toAdd[relativePath].CachePath = GetRelativePath(toAdd[relativePath].CachePath, GameDirectory);
                    foreach (var merge in toAdd[relativePath].Merges)
                        merge.Path = GetRelativePath(merge.Path, GameDirectory);
                }
            }

            foreach (var addKVP in toAdd)
                CachedEntries.Add(addKVP.Key, addKVP.Value);

            foreach (var path in toRemove)
                CachedEntries.Remove(path);
        }

        internal class CacheEntry
        {
            public string CachePath { get; set; }
            public DateTime OriginalTime { get; set; }
            public List<PathTimeTuple> Merges { get; set; } = new List<PathTimeTuple>();

            [JsonIgnore] internal string CacheAbsolutePath
            {
                get
                {
                    if (string.IsNullOrEmpty(cacheAbsolutePath))
                        cacheAbsolutePath = ResolvePath(CachePath, GameDirectory);

                    return cacheAbsolutePath;
                }
            }
            [JsonIgnore] private string cacheAbsolutePath;
            [JsonIgnore] internal bool CacheHit; // default is false
            [JsonIgnore] internal string ContainingDirectory;
            [JsonIgnore] internal bool HasErrors; // default is false


            [JsonConstructor]
            public CacheEntry()
            {
            }

            public CacheEntry(string absolutePath, string originalAbsolutePath, List<string> mergePaths)
            {
                cacheAbsolutePath = absolutePath;
                CachePath = GetRelativePath(absolutePath, GameDirectory);
                ContainingDirectory = Path.GetDirectoryName(absolutePath);
                OriginalTime = File.GetLastWriteTimeUtc(originalAbsolutePath);

                if (string.IsNullOrEmpty(ContainingDirectory))
                {
                    HasErrors = true;
                    return;
                }

                // get the parent JSON
                JObject parentJObj;
                try
                {
                    parentJObj = ParseGameJSONFile(originalAbsolutePath);
                }
                catch (Exception e)
                {
                    LogException($"\tParent JSON at path {originalAbsolutePath} has errors preventing any merges!", e);
                    HasErrors = true;
                    return;
                }

                foreach (var mergePath in mergePaths)
                    Merges.Add(new PathTimeTuple(GetRelativePath(mergePath, GameDirectory), File.GetLastWriteTimeUtc(mergePath)));

                Directory.CreateDirectory(ContainingDirectory);

                using (var writer = File.CreateText(absolutePath))
                {
                    // merge all of the merges
                    foreach (var mergePath in mergePaths)
                    {
                        JObject mergeJObj;
                        try
                        {
                            mergeJObj = ParseGameJSONFile(mergePath);
                        }
                        catch (Exception e)
                        {
                            LogException($"\tMod merge JSON at path {GetRelativePath(mergePath, GameDirectory)} has errors preventing its merge!", e);
                            continue;
                        }

                        if (AdvancedJSONMerger.IsAdvancedJSONMerge(mergeJObj))
                        {
                            try
                            {
                                AdvancedJSONMerger.ProcessInstructionsJObject(parentJObj, mergeJObj);
                                continue;
                            }
                            catch (Exception e)
                            {
                                LogException($"\tMod advanced merge JSON at path {GetRelativePath(mergePath, GameDirectory)} has errors preventing merge!", e);
                                continue;
                            }
                        }

                        // assume standard merging
                        parentJObj.Merge(mergeJObj, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                    }

                    // write the merged onto file to disk
                    var jsonWriter = new JsonTextWriter(writer)
                    {
                        Formatting = Formatting.Indented
                    };
                    parentJObj.WriteTo(jsonWriter);
                    jsonWriter.Close();
                }
            }

            internal bool MatchesPaths(string originalPath, List<string> mergePaths)
            {
                // must have an existing cached json
                if (!File.Exists(CacheAbsolutePath))
                    return false;

                // must have the same original file
                if (File.GetLastWriteTimeUtc(originalPath) != OriginalTime)
                    return false;

                // must match number of merges
                if (mergePaths.Count != Merges.Count)
                    return false;

                // if all paths match with write times, we match
                for (var index = 0; index < mergePaths.Count; index++)
                {
                    var mergeAbsolutePath = mergePaths[index];
                    var mergeTime = File.GetLastWriteTimeUtc(mergeAbsolutePath);
                    var cachedMergeAbsolutePath = ResolvePath(Merges[index].Path, GameDirectory);
                    var cachedMergeTime = Merges[index].Time;

                    if (mergeAbsolutePath != cachedMergeAbsolutePath || mergeTime != cachedMergeTime)
                        return false;
                }

                return true;
            }

            internal class PathTimeTuple
            {
                public PathTimeTuple(string path, DateTime time)
                {
                    Path = path;
                    Time = time;
                }

                public string Path { get; set; }
                public DateTime Time { get; set; }
            }
        }
    }
}
