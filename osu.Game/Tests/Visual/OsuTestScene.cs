// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Tests.Beatmaps;

namespace osu.Game.Tests.Visual
{
    public abstract class OsuTestScene : TestScene
    {
        [Cached(typeof(Bindable<WorkingBeatmap>))]
        [Cached(typeof(IBindable<WorkingBeatmap>))]
        private NonNullableBindable<WorkingBeatmap> beatmap;

        protected Bindable<WorkingBeatmap> Beatmap => beatmap;

        [Cached]
        [Cached(typeof(IBindable<RulesetInfo>))]
        protected readonly Bindable<RulesetInfo> Ruleset = new Bindable<RulesetInfo>();

        [Cached]
        [Cached(Type = typeof(IBindable<IReadOnlyList<Mod>>))]
        protected readonly Bindable<IReadOnlyList<Mod>> Mods = new Bindable<IReadOnlyList<Mod>>(Array.Empty<Mod>());

        protected new DependencyContainer Dependencies { get; private set; }

        private readonly Lazy<Storage> localStorage;
        protected Storage LocalStorage => localStorage.Value;

        private readonly Lazy<DatabaseContextFactory> contextFactory;

        protected IAPIProvider API
        {
            get
            {
                if (UseOnlineAPI)
                    throw new InvalidOperationException($"Using the {nameof(OsuTestScene)} dummy API is not supported when {nameof(UseOnlineAPI)} is true");

                return dummyAPI;
            }
        }

        private DummyAPIAccess dummyAPI;

        protected DatabaseContextFactory ContextFactory => contextFactory.Value;

        /// <summary>
        /// Whether this test scene requires real-world API access.
        /// If true, this will bypass the local <see cref="DummyAPIAccess"/> and use the <see cref="OsuGameBase"/> provided one.
        /// </summary>
        protected virtual bool UseOnlineAPI => false;

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            // This is the earliest we can get OsuGameBase, which is used by the dummy working beatmap to find textures
            var working = new DummyWorkingBeatmap(parent.Get<AudioManager>(), parent.Get<TextureStore>());

            beatmap = new NonNullableBindable<WorkingBeatmap>(working) { Default = working };
            beatmap.BindValueChanged(b => ScheduleAfterChildren(() =>
            {
                // compare to last beatmap as sometimes the two may share a track representation (optimisation, see WorkingBeatmap.TransferTo)
                if (b.OldValue?.TrackLoaded == true && b.OldValue?.Track != b.NewValue?.Track)
                    b.OldValue.RecycleTrack();
            }));

            Dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

            if (!UseOnlineAPI)
            {
                dummyAPI = new DummyAPIAccess();
                Dependencies.CacheAs<IAPIProvider>(dummyAPI);
                Add(dummyAPI);
            }

            return Dependencies;
        }

        protected override Container<Drawable> Content => content ?? base.Content;

        private readonly Container content;

        protected OsuTestScene()
        {
            localStorage = new Lazy<Storage>(() => new NativeStorage($"{GetType().Name}-{Guid.NewGuid()}"));
            contextFactory = new Lazy<DatabaseContextFactory>(() =>
            {
                var factory = new DatabaseContextFactory(LocalStorage);
                factory.ResetDatabase();
                using (var usage = factory.Get())
                    usage.Migrate();
                return factory;
            });

            base.Content.Add(content = new DrawSizePreservingFillContainer());
        }

        [Resolved]
        private AudioManager audio { get; set; }

        protected virtual IBeatmap CreateBeatmap(RulesetInfo ruleset) => new TestBeatmap(ruleset);

        protected WorkingBeatmap CreateWorkingBeatmap(RulesetInfo ruleset) =>
            CreateWorkingBeatmap(CreateBeatmap(ruleset));

        protected virtual WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap) =>
            new ClockBackedTestWorkingBeatmap(beatmap, Clock, audio);

        [BackgroundDependencyLoader]
        private void load(RulesetStore rulesets)
        {
            Ruleset.Value = rulesets.AvailableRulesets.First();
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (beatmap?.Value.TrackLoaded == true)
                beatmap.Value.Track.Stop();

            if (contextFactory.IsValueCreated)
                contextFactory.Value.ResetDatabase();

            if (localStorage.IsValueCreated)
            {
                try
                {
                    localStorage.Value.DeleteDirectory(".");
                }
                catch
                {
                    // we don't really care if this fails; it will just leave folders lying around from test runs.
                }
            }
        }

        protected override ITestSceneTestRunner CreateRunner() => new OsuTestSceneTestRunner();

        public class ClockBackedTestWorkingBeatmap : TestWorkingBeatmap
        {
            private readonly Track track;

            private readonly TrackVirtualStore store;

            /// <summary>
            /// Create an instance which creates a <see cref="TestBeatmap"/> for the provided ruleset when requested.
            /// </summary>
            /// <param name="ruleset">The target ruleset.</param>
            /// <param name="referenceClock">A clock which should be used instead of a stopwatch for virtual time progression.</param>
            /// <param name="audio">Audio manager. Required if a reference clock isn't provided.</param>
            public ClockBackedTestWorkingBeatmap(RulesetInfo ruleset, IFrameBasedClock referenceClock, AudioManager audio)
                : this(new TestBeatmap(ruleset), referenceClock, audio)
            {
            }

            /// <summary>
            /// Create an instance which provides the <see cref="IBeatmap"/> when requested.
            /// </summary>
            /// <param name="beatmap">The beatmap</param>
            /// <param name="referenceClock">An optional clock which should be used instead of a stopwatch for virtual time progression.</param>
            /// <param name="audio">Audio manager. Required if a reference clock isn't provided.</param>
            /// <param name="length">The length of the returned virtual track.</param>
            public ClockBackedTestWorkingBeatmap(IBeatmap beatmap, IFrameBasedClock referenceClock, AudioManager audio, double length = 60000)
                : base(beatmap)
            {
                if (referenceClock != null)
                {
                    store = new TrackVirtualStore(referenceClock);
                    audio.AddItem(store);
                    track = store.GetVirtual(length);
                }
                else
                    track = audio?.Tracks.GetVirtual(length);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);
                store?.Dispose();
            }

            protected override Track GetTrack() => track;

            public class TrackVirtualStore : AudioCollectionManager<Track>, ITrackStore
            {
                private readonly IFrameBasedClock referenceClock;

                public TrackVirtualStore(IFrameBasedClock referenceClock)
                {
                    this.referenceClock = referenceClock;
                }

                public Track Get(string name) => throw new NotImplementedException();

                public Task<Track> GetAsync(string name) => throw new NotImplementedException();

                public Stream GetStream(string name) => throw new NotImplementedException();

                public IEnumerable<string> GetAvailableResources() => throw new NotImplementedException();

                public Track GetVirtual(double length = Double.PositiveInfinity)
                {
                    var track = new TrackVirtualManual(referenceClock) { Length = length };
                    AddItem(track);
                    return track;
                }
            }

            /// <summary>
            /// A virtual track which tracks a reference clock.
            /// </summary>
            public class TrackVirtualManual : Track
            {
                private readonly IFrameBasedClock referenceClock;

                private readonly ManualClock clock = new ManualClock();

                private bool running;

                /// <summary>
                /// Local offset added to the reference clock to resolve correct time.
                /// </summary>
                private double offset;

                public TrackVirtualManual(IFrameBasedClock referenceClock)
                {
                    this.referenceClock = referenceClock;
                    Length = double.PositiveInfinity;
                }

                public override bool Seek(double seek)
                {
                    offset = Math.Clamp(seek, 0, Length);
                    lastReferenceTime = null;

                    return offset == seek;
                }

                public override void Start()
                {
                    running = true;
                }

                public override void Reset()
                {
                    Seek(0);
                    base.Reset();
                }

                public override void Stop()
                {
                    if (running)
                    {
                        running = false;
                        // on stopping, the current value should be transferred out of the clock, as we can no longer rely on
                        // the referenceClock (which will still be counting time).
                        offset = clock.CurrentTime;
                        lastReferenceTime = null;
                    }
                }

                public override bool IsRunning => running;

                private double? lastReferenceTime;

                public override double CurrentTime => clock.CurrentTime;

                protected override void UpdateState()
                {
                    base.UpdateState();

                    if (running)
                    {
                        double refTime = referenceClock.CurrentTime;

                        if (!lastReferenceTime.HasValue)
                        {
                            // if the clock just started running, the current value should be transferred to the offset
                            // (to zero the progression of time).
                            offset -= refTime;
                        }

                        lastReferenceTime = refTime;
                    }

                    clock.CurrentTime = Math.Min((lastReferenceTime ?? 0) + offset, Length);

                    if (CurrentTime >= Length)
                    {
                        Stop();
                        RaiseCompleted();
                    }
                }
            }
        }

        public class OsuTestSceneTestRunner : OsuGameBase, ITestSceneTestRunner
        {
            private TestSceneTestRunner.TestRunner runner;

            protected override void LoadAsyncComplete()
            {
                // this has to be run here rather than LoadComplete because
                // TestScene.cs is checking the IsLoaded state (on another thread) and expects
                // the runner to be loaded at that point.
                Add(runner = new TestSceneTestRunner.TestRunner());
            }

            public void RunTestBlocking(TestScene test) => runner.RunTestBlocking(test);
        }
    }
}
