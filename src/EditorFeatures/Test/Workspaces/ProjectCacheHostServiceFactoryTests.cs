﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Options.Providers;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.UnitTests;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces
{
    [UseExportProvider]
    public class ProjectCacheHostServiceFactoryTests
    {
        private static void Test(Action<IProjectCacheHostService, ProjectId, ICachedObjectOwner, ObjectReference<object>> action)
        {
            // Putting cacheService.CreateStrongReference in a using statement
            // creates a temporary local that isn't collected in Debug builds
            // Wrapping it in a lambda allows it to get collected.
            var cacheService = new ProjectCacheService(null, createImplicitCache: true);
            var projectId = ProjectId.CreateNewId();
            var owner = new Owner();
            var instance = ObjectReference.CreateFromFactory(() => new object());

            action(cacheService, projectId, owner, instance);
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestCacheKeepsObjectAlive1()
        {
            Test((cacheService, projectId, owner, instance) =>
            {
                using (cacheService.EnableCaching(projectId))
                {
                    instance.UseReference(i => cacheService.CacheObjectIfCachingEnabledForKey(projectId, (object)owner, i));

                    instance.AssertHeld();
                }

                instance.AssertReleased();

                GC.KeepAlive(owner);
            });
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestCacheKeepsObjectAlive2()
        {
            Test((cacheService, projectId, owner, instance) =>
            {
                using (cacheService.EnableCaching(projectId))
                {
                    instance.UseReference(i => cacheService.CacheObjectIfCachingEnabledForKey(projectId, owner, i));

                    instance.AssertHeld();
                }

                instance.AssertReleased();

                GC.KeepAlive(owner);
            });
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestCacheDoesNotKeepObjectsAliveAfterOwnerIsCollected1()
        {
            Test((cacheService, projectId, owner, instance) =>
            {
                using (cacheService.EnableCaching(projectId))
                {
                    cacheService.CacheObjectIfCachingEnabledForKey(projectId, (object)owner, instance);
                    owner = null;

                    instance.AssertReleased();
                }
            });
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestCacheDoesNotKeepObjectsAliveAfterOwnerIsCollected2()
        {
            Test((cacheService, projectId, owner, instance) =>
            {
                using (cacheService.EnableCaching(projectId))
                {
                    cacheService.CacheObjectIfCachingEnabledForKey(projectId, owner, instance);
                    owner = null;

                    instance.AssertReleased();
                }
            });
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestImplicitCacheKeepsObjectAlive1()
        {
            var workspace = new AdhocWorkspace(MockHostServices.Instance, workspaceKind: WorkspaceKind.Host);
            var cacheService = new ProjectCacheService(workspace, createImplicitCache: true);
            var reference = ObjectReference.CreateFromFactory(() => new object());
            reference.UseReference(r => cacheService.CacheObjectIfCachingEnabledForKey(ProjectId.CreateNewId(), (object)null, r));
            reference.AssertHeld();

            GC.KeepAlive(cacheService);
        }

        [Fact(Skip = "https://github.com/dotnet/roslyn/issues/14592")]
        public void TestImplicitCacheMonitoring()
        {
            var workspace = new AdhocWorkspace(MockHostServices.Instance, workspaceKind: WorkspaceKind.Host);
            var cacheService = new ProjectCacheService(workspace, createImplicitCache: true);
            var weak = PutObjectInImplicitCache(cacheService);

            weak.AssertReleased();

            GC.KeepAlive(cacheService);
        }

        private static ObjectReference<object> PutObjectInImplicitCache(ProjectCacheService cacheService)
        {
            var reference = ObjectReference.CreateFromFactory(() => new object());

            reference.UseReference(r => cacheService.CacheObjectIfCachingEnabledForKey(ProjectId.CreateNewId(), (object)null, r));

            return reference;
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestP2PReference()
        {
            var workspace = new AdhocWorkspace();

            var project1 = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "proj1", "proj1", LanguageNames.CSharp);
            var project2 = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "proj2", "proj2", LanguageNames.CSharp, projectReferences: SpecializedCollections.SingletonEnumerable(new ProjectReference(project1.Id)));
            var solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, projects: new ProjectInfo[] { project1, project2 });

            var solution = workspace.AddSolution(solutionInfo);

            var instanceTracker = ObjectReference.CreateFromFactory(() => new object());

            var cacheService = new ProjectCacheService(workspace, createImplicitCache: true);
            using (var cache = cacheService.EnableCaching(project2.Id))
            {
                instanceTracker.UseReference(r => cacheService.CacheObjectIfCachingEnabledForKey(project1.Id, (object)null, r));
                solution = null;

                workspace.OnProjectRemoved(project1.Id);
                workspace.OnProjectRemoved(project2.Id);
            }

            // make sure p2p reference doesn't go to implicit cache
            instanceTracker.AssertReleased();
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestEjectFromImplicitCache()
        {
            var compilations = new List<Compilation>();
            for (var i = 0; i < ProjectCacheService.ImplicitCacheSize + 1; i++)
            {
                compilations.Add(CSharpCompilation.Create(i.ToString()));
            }

            var weakFirst = ObjectReference.Create(compilations[0]);
            var weakLast = ObjectReference.Create(compilations[compilations.Count - 1]);

            var workspace = new AdhocWorkspace(MockHostServices.Instance, workspaceKind: WorkspaceKind.Host);
            var cache = new ProjectCacheService(workspace, createImplicitCache: true);
            for (var i = 0; i < ProjectCacheService.ImplicitCacheSize + 1; i++)
            {
                cache.CacheObjectIfCachingEnabledForKey(ProjectId.CreateNewId(), (object)null, compilations[i]);
            }

#pragma warning disable IDE0059 // Unnecessary assignment of a value - testing weak reference to compilations
            compilations = null;
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            weakFirst.AssertReleased();
            weakLast.AssertHeld();

            GC.KeepAlive(cache);
        }

        [WorkItem(28639, "https://github.com/dotnet/roslyn/issues/28639")]
        [ConditionalFact(typeof(Bitness32))]
        public void TestCacheCompilationTwice()
        {
            var comp1 = CSharpCompilation.Create("1");
            var comp2 = CSharpCompilation.Create("2");
            var comp3 = CSharpCompilation.Create("3");

            var weak3 = ObjectReference.Create(comp3);
            var weak1 = ObjectReference.Create(comp1);

            var workspace = new AdhocWorkspace(MockHostServices.Instance, workspaceKind: WorkspaceKind.Host);
            var cache = new ProjectCacheService(workspace, createImplicitCache: true);
            var key = ProjectId.CreateNewId();
            var owner = new object();
            cache.CacheObjectIfCachingEnabledForKey(key, owner, comp1);
            cache.CacheObjectIfCachingEnabledForKey(key, owner, comp2);
            cache.CacheObjectIfCachingEnabledForKey(key, owner, comp3);

            // When we cache 3 again, 1 should stay in the cache
            cache.CacheObjectIfCachingEnabledForKey(key, owner, comp3);
#pragma warning disable IDE0059 // Unnecessary assignment of a value - testing weak references to compilations
            comp1 = null;
            comp2 = null;
            comp3 = null;
#pragma warning restore IDE0059 // Unnecessary assignment of a value

            weak3.AssertHeld();
            weak1.AssertHeld();

            GC.KeepAlive(cache);
        }

        private class Owner : ICachedObjectOwner
        {
            object ICachedObjectOwner.CachedObject { get; set; }
        }

        private class MockHostServices : HostServices
        {
            public static readonly MockHostServices Instance = new MockHostServices();

            private MockHostServices() { }

            protected internal override HostWorkspaceServices CreateWorkspaceServices(Workspace workspace)
                => new MockHostWorkspaceServices(this, workspace);
        }

        private sealed class MockTaskSchedulerProvider : ITaskSchedulerProvider
        {
            public TaskScheduler CurrentContextScheduler
                => (SynchronizationContext.Current != null) ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
        }

        private sealed class MockWorkspaceAsynchronousOperationListenerProvider : IWorkspaceAsynchronousOperationListenerProvider
        {
            public IAsynchronousOperationListener GetListener()
                => AsynchronousOperationListenerProvider.NullListener;
        }

        private class MockHostWorkspaceServices : HostWorkspaceServices
        {
            private readonly HostServices _hostServices;
            private readonly Workspace _workspace;
            private readonly ILegacyWorkspaceOptionService _optionService = new MockOptionService();

            private static readonly ITaskSchedulerProvider s_taskSchedulerProvider = new MockTaskSchedulerProvider();
            private static readonly IWorkspaceAsynchronousOperationListenerProvider s_asyncListenerProvider = new MockWorkspaceAsynchronousOperationListenerProvider();

            public MockHostWorkspaceServices(HostServices hostServices, Workspace workspace)
            {
                _hostServices = hostServices;
                _workspace = workspace;
            }

            public override HostServices HostServices => _hostServices;

            public override Workspace Workspace => _workspace;

            public override IEnumerable<TLanguageService> FindLanguageServices<TLanguageService>(MetadataFilter filter)
                => ImmutableArray<TLanguageService>.Empty;

            public override TWorkspaceService GetService<TWorkspaceService>()
            {
                if (s_taskSchedulerProvider is TWorkspaceService)
                {
                    return (TWorkspaceService)s_taskSchedulerProvider;
                }

                if (s_asyncListenerProvider is TWorkspaceService)
                {
                    return (TWorkspaceService)s_asyncListenerProvider;
                }

                if (_optionService is TWorkspaceService workspaceOptionService)
                {
                    return workspaceOptionService;
                }

                return default;
            }

            private sealed class MockOptionService : ILegacyWorkspaceOptionService
            {
                public IGlobalOptionService GlobalOptions { get; } =
                    new GlobalOptionService(workspaceThreadingService: null, ImmutableArray<Lazy<IOptionPersisterProvider>>.Empty);

                public void RegisterWorkspace(Workspace workspace)
                {
                }

                public void UnregisterWorkspace(Workspace workspace)
                {
                }

                public object GetOption(OptionKey key)
                    => throw new NotImplementedException();

                public void SetOptions(OptionSet optionSet, IEnumerable<OptionKey> optionKeys)
                    => throw new NotImplementedException();
            }
        }
    }
}
