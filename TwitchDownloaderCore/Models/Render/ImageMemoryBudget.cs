using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Models.Render
{
    /// <summary>
    /// Tracks how much memory decoded emote and GIF frames are using, and releases the least recently drawn
    /// off-screen ones when the total exceeds a budget.
    /// </summary>
    public sealed class ImageMemoryBudget
    {
        private readonly object _syncLock = new();
        private readonly long _budgetBytes;
        private readonly HashSet<TwitchEmote> _trackedEmotes = [];
        private readonly Dictionary<int, HashSet<TwitchEmote>> _workerActiveSets = [];
        private readonly List<TwitchEmote> _evictionCandidates = [];
        private long _tick;

        public ImageMemoryBudget(long budgetBytes)
        {
            _budgetBytes = budgetBytes;
        }

        public void MarkDrawn(IEnumerable<TwitchEmote> emotes)
        {
            lock (_syncLock)
            {
                _tick++;
                foreach (var emote in emotes)
                {
                    emote.LastUsedTick = _tick;
                    _trackedEmotes.Add(emote);
                }
            }
        }

        public void MarkDrawn(List<EmotePosition> emotePositions, int workerId = 0)
        {
            if (emotePositions.Count == 0)
            {
                return;
            }

            lock (_syncLock)
            {
                _tick++;
                if (!_workerActiveSets.TryGetValue(workerId, out var workerSet))
                {
                    workerSet = [];
                    _workerActiveSets[workerId] = workerSet;
                }

                for (int i = 0; i < emotePositions.Count; i++)
                {
                    var emote = emotePositions[i].Emote;
                    emote.LastUsedTick = _tick;
                    _trackedEmotes.Add(emote);
                    workerSet.Add(emote);
                }
            }
        }

        /// <summary>
        /// Removes a completed worker's active emote set so its emotes can be evicted if needed by remaining workers.
        /// </summary>
        public void UnregisterWorker(int workerId)
        {
            lock (_syncLock)
            {
                _workerActiveSets.Remove(workerId);
            }
        }

        /// <summary>
        /// Releases off-screen tracked images oldest-first until the remaining off-screen total is within budget.
        /// In multi-worker renders, any emote active in any worker's active set is protected from eviction.
        /// </summary>
        public bool ReleaseUnusedTracked(HashSet<TwitchEmote> onScreen, int workerId = 0)
        {
            lock (_syncLock)
            {
                if (!_workerActiveSets.TryGetValue(workerId, out var workerSet))
                {
                    workerSet = [];
                    _workerActiveSets[workerId] = workerSet;
                }

                workerSet.Clear();
                foreach (var emote in onScreen)
                {
                    workerSet.Add(emote);
                }

                return ReleaseUnusedCore(_trackedEmotes, onScreen, checkAllWorkers: _workerActiveSets.Count > 1);
            }
        }

        /// <summary>
        /// Releases off-screen images oldest-first until the remaining off-screen total is within budget.
        /// Images in <paramref name="onScreen"/> are neither released nor counted against the budget, so a
        /// single large on-screen GIF cannot force every off-screen emote to be evicted and re-decoded on a loop.
        /// </summary>
        /// <returns><see langword="true"/> if at least one image had its frames released.</returns>
        public bool ReleaseUnused(IEnumerable<TwitchEmote> decoded, HashSet<TwitchEmote> onScreen)
        {
            lock (_syncLock)
            {
                return ReleaseUnusedCore(decoded, onScreen, checkAllWorkers: false);
            }
        }

        private bool IsActiveInAnyWorker(TwitchEmote image, HashSet<TwitchEmote> primaryOnScreen, bool checkAllWorkers)
        {
            if (primaryOnScreen.Contains(image))
            {
                return true;
            }

            if (!checkAllWorkers)
            {
                return false;
            }

            foreach (var activeSet in _workerActiveSets.Values)
            {
                if (activeSet.Contains(image))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ReleaseUnusedCore(IEnumerable<TwitchEmote> decoded, HashSet<TwitchEmote> onScreen, bool checkAllWorkers)
        {
            long offScreenBytes = 0;
            _evictionCandidates.Clear();

            foreach (var image in decoded)
            {
                if (!image.FramesMaterialized || IsActiveInAnyWorker(image, onScreen, checkAllWorkers))
                {
                    continue;
                }

                offScreenBytes += image.DecodedByteSize;
                _evictionCandidates.Add(image);
            }

            if (offScreenBytes <= _budgetBytes)
            {
                return false;
            }

            _evictionCandidates.Sort(static (a, b) => a.LastUsedTick.CompareTo(b.LastUsedTick));

            var releasedAny = false;
            foreach (var image in _evictionCandidates)
            {
                if (offScreenBytes <= _budgetBytes)
                {
                    break;
                }

                offScreenBytes -= image.DecodedByteSize;
                image.ReleaseFrames();
                releasedAny = true;
            }

            return releasedAny;
        }
    }
}
