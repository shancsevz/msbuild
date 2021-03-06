// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

namespace Microsoft.Build.UnitTests.Definition
{
    /// <summary>
    ///     Tests some manipulations of Project and ProjectCollection that require dealing with internal data.
    /// </summary>
    public class ProjectEvaluationContext_Tests : IDisposable
    {
        public ProjectEvaluationContext_Tests()
        {
            _env = TestEnvironment.Create();

            _resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new Dictionary<string, SdkResult>
                {
                    {"foo", new SdkResult(new SdkReference("foo", "1.0.0", null), "path", "1.0.0", null)},
                    {"bar", new SdkResult(new SdkReference("bar", "1.0.0", null), "path", "1.0.0", null)}
                });
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        private readonly SdkUtilities.ConfigurableMockSdkResolver _resolver;
        private readonly TestEnvironment _env;

        private static void SetResolverForContext(EvaluationContext context, SdkResolver resolver)
        {
            var sdkService = (SdkResolverService) context.SdkResolverService;

            sdkService.InitializeForTests(null, new List<SdkResolver> {resolver});
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void SharedContextShouldGetReusedWhereasIsolatedContextShouldNot(EvaluationContext.SharingPolicy policy)
        {
            var previousContext = EvaluationContext.Create(policy);

            for (var i = 0; i < 10; i++)
            {
                var currentContext = previousContext.ContextForNewProject();

                if (i == 0)
                {
                    currentContext.ShouldBeSameAs(previousContext, "first usage context was not the same as the initial context");
                }
                else
                {
                    switch (policy)
                    {
                        case EvaluationContext.SharingPolicy.Shared:
                            currentContext.ShouldBeSameAs(previousContext, $"Shared policy: usage {i} was not the same as usage {i - 1}");
                            break;
                        case EvaluationContext.SharingPolicy.Isolated:
                            currentContext.ShouldNotBeSameAs(previousContext, $"Isolated policy: usage {i} was the same as usage {i - 1}");
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                    }
                }

                previousContext = currentContext;
            }
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void ReevaluationShouldNotReuseInitialContext(EvaluationContext.SharingPolicy policy)
        {
            try
            {
                EvaluationContext.TestOnlyHookOnCreate = c => SetResolverForContext(c, _resolver);

                var collection = _env.CreateProjectCollection().Collection;

                var context = EvaluationContext.Create(policy);

                var project = Project.FromXmlReader(
                    XmlReader.Create(new StringReader("<Project Sdk=\"foo\"></Project>")),
                    new ProjectOptions
                    {
                        ProjectCollection = collection,
                        EvaluationContext = context,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                    });

                _resolver.ResolvedCalls["foo"].ShouldBe(1);

                project.AddItem("a", "b");

                project.ReevaluateIfNecessary();

                _resolver.ResolvedCalls["foo"].ShouldBe(2);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        private static string[] _sdkResolutionProjects =
        {
            "<Project Sdk=\"foo\"></Project>",
            "<Project Sdk=\"bar\"></Project>",
            "<Project Sdk=\"foo\"></Project>",
            "<Project Sdk=\"bar\"></Project>"
        };

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared, 1, 1)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated, 4, 4)]
        public void ContextPinsSdkResolverCache(EvaluationContext.SharingPolicy policy, int sdkLookupsForFoo, int sdkLookupsForBar)
        {
            try
            {
                EvaluationContext.TestOnlyHookOnCreate = c => SetResolverForContext(c, _resolver);

                var context = EvaluationContext.Create(policy);
                EvaluateProjects(_sdkResolutionProjects, context, null);

                _resolver.ResolvedCalls.Count.ShouldBe(2);
                _resolver.ResolvedCalls["foo"].ShouldBe(sdkLookupsForFoo);
                _resolver.ResolvedCalls["bar"].ShouldBe(sdkLookupsForBar);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        [Fact]
        public void DefaultContextIsIsolatedContext()
        {
            try
            {
                var seenContexts = new HashSet<EvaluationContext>();

                EvaluationContext.TestOnlyHookOnCreate = c => seenContexts.Add(c);

                EvaluateProjects(_sdkResolutionProjects, null, null);

                seenContexts.Count.ShouldBe(8); // 4 evaluations and 4 reevaluations
                seenContexts.ShouldAllBe(c => c.Policy == EvaluationContext.SharingPolicy.Isolated);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        public static IEnumerable<object> ContextPinsGlobExpansionCacheData
        {
            get
            {
                yield return new object[]
                {
                    EvaluationContext.SharingPolicy.Shared,
                    new[]
                    {
                        new[] {"0.cs"},
                        new[] {"0.cs"},
                        new[] {"0.cs"},
                        new[] {"0.cs"}
                    }
                };

                yield return new object[]
                {
                    EvaluationContext.SharingPolicy.Isolated,
                    new[]
                    {
                        new[] {"0.cs"},
                        new[] {"0.cs", "1.cs"},
                        new[] {"0.cs", "1.cs", "2.cs"},
                        new[] {"0.cs", "1.cs", "2.cs", "3.cs"},
                    }
                };
            }
        }

        private static string[] _projectsWithGlobs =
        {
            @"<Project>
                <ItemGroup>
                    <i Include=`**/*.cs` />
                </ItemGroup>
            </Project>",

            @"<Project>
                <ItemGroup>
                    <i Include=`**/*.cs` />
                </ItemGroup>
            </Project>",
        };

        [Theory]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        public void ContextCachesItemElementGlobExpansions(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var projectDirectory = _env.DefaultTestDirectory.FolderPath;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                _projectsWithGlobs,
                context,
                project =>
                {
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.cs"), "");

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                }
                );
        }

        private static string[] _projectsWithOutOfConeGlobs =
        {
            @"<Project>
                <ItemGroup>
                    <i Include=`{0}**/*.cs` />
                </ItemGroup>
            </Project>",

            @"<Project>
                <ItemGroup>
                    <i Include=`{0}**/*.cs` />
                </ItemGroup>
            </Project>",
        };

        public static IEnumerable<object> ContextCachesCommonOutOfProjectConeGlobData
        {
            get
            {
                // combine the globbing test data with another bool for relative / absolute itemspecs
                foreach (var itemSpecPathIsRelative in new []{true, false})
                {
                    foreach (var globData in ContextPinsGlobExpansionCacheData)
                    {
                        var globDataArray = (object[]) globData;

                        yield return new[]
                        {
                            itemSpecPathIsRelative,
                            globDataArray[0],
                            globDataArray[1],
                        };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(ContextCachesCommonOutOfProjectConeGlobData))]
        // projects should cache glob expansions when the glob is shared between projects and points outside of project cone
        public void ContextCachesCommonOutOfProjectConeGlob(bool itemSpecPathIsRelative, EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var testDirectory = _env.DefaultTestDirectory.FolderPath;
            var globDirectory = Path.Combine(testDirectory, "GlobDirectory");

            var itemSpecDirectoryPart = itemSpecPathIsRelative
                ? Path.Combine("..", "GlobDirectory")
                : globDirectory;

            itemSpecDirectoryPart = itemSpecDirectoryPart.WithTrailingSlash();

            Directory.CreateDirectory(globDirectory);

            // Globs with a directory part will produce items prepended with that directory part
            foreach (var globExpansion in expectedGlobExpansions)
            {
                for (var i = 0; i < globExpansion.Length; i++)
                {
                    globExpansion[i] = Path.Combine(itemSpecDirectoryPart, globExpansion[i]);
                }
            }

            var projectSpecs = _projectsWithOutOfConeGlobs
                .Select(p => string.Format(p, itemSpecDirectoryPart))
                .Select((p, i) => new ProjectSpecification(Path.Combine(testDirectory, $"ProjectDirectory{i}", $"Project{i}.proj"), p));

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(globDirectory, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                projectSpecs,
                context,
                project =>
                {
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(globDirectory, $"{evaluationCount}.cs"), "");

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                }
                );
        }

        private static string[] _projectsWithGlobImports =
        {
            @"<Project>
                <Import Project=`*.props` />
            </Project>",

            @"<Project>
                <Import Project=`*.props` />
            </Project>",
        };

        [Theory]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        public void ContextCachesImportGlobExpansions(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var projectDirectory = _env.DefaultTestDirectory.FolderPath;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.props"), $"<Project><ItemGroup><i Include=`{evaluationCount}.cs`/></ItemGroup></Project>".Cleanup());

            EvaluateProjects(
                _projectsWithGlobImports,
                context,
                project =>
                {
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.props"), $"<Project><ItemGroup><i Include=`{evaluationCount}.cs`/></ItemGroup></Project>".Cleanup());

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                }
                );
        }

        private static string[] _projectsWithConditions =
        {
            @"<Project>
                <PropertyGroup Condition=`Exists('0.cs')`>
                    <p>val</p>
                </PropertyGroup>
            </Project>",

            @"<Project>
                <PropertyGroup Condition=`Exists('0.cs')`>
                    <p>val</p>
                </PropertyGroup>
            </Project>",
        };

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        public void ContextCachesExistenceChecksInConditions(EvaluationContext.SharingPolicy policy)
        {
            var projectDirectory = _env.DefaultTestDirectory.FolderPath;

            var context = EvaluationContext.Create(policy);

            var theFile = Path.Combine(projectDirectory, "0.cs");
            File.WriteAllText(theFile, "");

            var evaluationCount = 0;

            EvaluateProjects(
                _projectsWithConditions,
                context,
                project =>
                {
                    evaluationCount++;

                    if (File.Exists(theFile))
                    {
                        File.Delete(theFile);
                    }

                    if (evaluationCount == 1)
                    {
                        project.GetPropertyValue("p").ShouldBe("val");
                    }
                    else
                        switch (policy)
                        {
                            case EvaluationContext.SharingPolicy.Shared:
                                project.GetPropertyValue("p").ShouldBe("val");
                                break;
                            case EvaluationContext.SharingPolicy.Isolated:
                                project.GetPropertyValue("p").ShouldBeEmpty();
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                        }
                }
                );
        }

        private void EvaluateProjects(IEnumerable<string> projectContents, EvaluationContext context, Action<Project> afterEvaluationAction)
        {
            EvaluateProjects(
                projectContents.Select((p, i) => new ProjectSpecification(Path.Combine(_env.DefaultTestDirectory.FolderPath, $"Project{i}.proj"), p)),
                context,
                afterEvaluationAction);
        }

        private struct ProjectSpecification
        {
            public string ProjectPath { get; }
            public string ProjectContents { get; }

            public ProjectSpecification(string projectPath, string projectContents)
            {
                ProjectPath = projectPath;
                ProjectContents = projectContents;
            }

            public void Deconstruct(out string projectPath, out string projectContents)
            {
                projectPath = this.ProjectPath;
                projectContents = this.ProjectContents;
            }
        }

        /// <summary>
        /// Should be at least two test projects to test cache visibility between projects
        /// </summary>
        private void EvaluateProjects(IEnumerable<ProjectSpecification> projectSpecs, EvaluationContext context, Action<Project> afterEvaluationAction)
        {
            var collection = _env.CreateProjectCollection().Collection;

            var projects = new List<Project>();

            foreach (var (projectPath, projectContents) in projectSpecs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(projectPath));
                File.WriteAllText(projectPath, projectContents.Cleanup());

                var project = Project.FromFile(
                    projectPath,
                    new ProjectOptions
                    {
                        ProjectCollection = collection,
                        EvaluationContext = context,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                    });

                afterEvaluationAction?.Invoke(project);

                projects.Add(project);
            }

            foreach (var project in projects)
            {
                project.AddItem("a", "b");
                project.ReevaluateIfNecessary(context);

                afterEvaluationAction?.Invoke(project);
            }
        }
    }
}
