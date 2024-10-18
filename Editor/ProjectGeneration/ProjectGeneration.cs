using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Profiling;
using SR = System.Reflection;

namespace VSCodeEditor
{
    public interface IGenerator
    {
        bool SyncIfNeeded(List<string> affectedFiles, string[] reimportedFiles);
        void Sync();
        string SolutionFile();
        string ProjectDirectory { get; }
        string CSharpProjFoldersDirectory { get; }
        IAssemblyNameProvider AssemblyNameProvider { get; }
        bool SolutionExists();
    }

    public class ProjectGeneration : IGenerator
    {
        enum ScriptingLanguage
        {
            None = 0,
            CSharp = 1
        }

        const string k_WindowsNewline = "\r\n";

        /// <summary>
        /// Map source extensions to ScriptingLanguages
        /// </summary>
        static readonly Dictionary<string, ScriptingLanguage> k_BuiltinSupportedExtensions =
            new()
            {
                { "cginc", ScriptingLanguage.None },
                { "compute", ScriptingLanguage.None },
                { "cs", ScriptingLanguage.CSharp },
                { "glslinc", ScriptingLanguage.None },
                { "hlsl", ScriptingLanguage.None },
                { "raytrace", ScriptingLanguage.None },
                { "shader", ScriptingLanguage.None },
                { "template", ScriptingLanguage.None },
                { "uss", ScriptingLanguage.None },
                { "uxml", ScriptingLanguage.None }
            };

        readonly string m_SolutionProjectEntryTemplate = string.Join(
                "\r\n",
                @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""",
                "EndProject"
            )
            .Replace("    ", "\t");

        readonly string m_SolutionProjectConfigurationTemplate = string.Join(
                "\r\n",
                "        {{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                "        {{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU"
            )
            .Replace("    ", "\t");

        static readonly string[] k_ReimportSyncExtensions = { ".dll", ".asmdef" };

        string[] m_ProjectSupportedExtensions = Array.Empty<string>();

        const string m_TargetCSharpProjFolders = "CSharpProjFolders";

        public string ProjectDirectory { get; }
        public string CSharpProjFoldersDirectory =>
            Path.Combine(ProjectDirectory, m_TargetCSharpProjFolders);
        IAssemblyNameProvider IGenerator.AssemblyNameProvider => m_AssemblyNameProvider;

        readonly string m_ProjectName;
        readonly IAssemblyNameProvider m_AssemblyNameProvider;
        readonly IFileIO m_FileIOProvider;
        readonly IGUIDGenerator m_GUIDProvider;

        const string k_TargetFrameworkVersion = "net48";

        public ProjectGeneration(string tempDirectory)
            : this(
                tempDirectory,
                new AssemblyNameProvider(),
                new FileIOProvider(),
                new GUIDProvider()
            )
        { }

        public ProjectGeneration(
            string tempDirectory,
            IAssemblyNameProvider assemblyNameProvider,
            IFileIO fileIO,
            IGUIDGenerator guidGenerator
        )
        {
            ProjectDirectory = tempDirectory.NormalizePath();
            m_ProjectName = Path.GetFileName(ProjectDirectory);
            m_AssemblyNameProvider = assemblyNameProvider;
            m_FileIOProvider = fileIO;
            m_GUIDProvider = guidGenerator;

            if (!m_FileIOProvider.DirectoryExists(CSharpProjFoldersDirectory))
            {
                m_FileIOProvider.CreateDirectory(CSharpProjFoldersDirectory);
            }
        }

        /// <summary>
        /// Syncs the scripting solution if any affected files are relevant.
        /// </summary>
        /// <returns>
        /// Whether the solution was synced.
        /// </returns>
        /// <param name='affectedFiles'>
        /// A set of files whose status has changed
        /// </param>
        /// <param name="reimportedFiles">
        /// A set of files that got reimported
        /// </param>
        public bool SyncIfNeeded(List<string> affectedFiles, string[] reimportedFiles)
        {
            Profiler.BeginSample("SolutionSynchronizerSync");
            SetupProjectSupportedExtensions();

            if (!HasFilesBeenModified(affectedFiles, reimportedFiles))
            {
                Profiler.EndSample();
                return false;
            }

            var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution);
            var allProjectAssemblies = RelevantAssembliesForMode(assemblies).ToList();
            SyncSolution(allProjectAssemblies);

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            var affectedNames = affectedFiles
                .Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(
                    name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]
                );
            var reimportedNames = reimportedFiles
                .Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(
                    name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]
                );
            var affectedAndReimported = new HashSet<string>(affectedNames.Concat(reimportedNames));

            foreach (var assembly in allProjectAssemblies)
            {
                if (!affectedAndReimported.Contains(assembly.name))
                    continue;

                SyncProject(assembly, allAssetProjectParts, ParseResponseFileData(assembly));
            }

            Profiler.EndSample();
            return true;
        }

        bool HasFilesBeenModified(List<string> affectedFiles, string[] reimportedFiles)
        {
            return affectedFiles.Any(ShouldFileBePartOfSolution)
                || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        }

        static bool ShouldSyncOnReimportedAsset(string asset)
        {
            return k_ReimportSyncExtensions.Contains(new FileInfo(asset).Extension);
        }

        private static IEnumerable<SR.MethodInfo> GetPostProcessorCallbacks(string name)
        {
            return TypeCache
                .GetTypesDerivedFrom<AssetPostprocessor>()
                .Select(
                    t =>
                        t.GetMethod(
                            name,
                            SR.BindingFlags.Public
                                | SR.BindingFlags.NonPublic
                                | SR.BindingFlags.Static
                        )
                )
                .Where(m => m != null);
        }

        static void OnGeneratedCSProjectFiles()
        {
            foreach (var method in GetPostProcessorCallbacks(nameof(OnGeneratedCSProjectFiles)))
            {
                _ = method.Invoke(null, Array.Empty<object>());
            }
        }

        private static string InvokeAssetPostProcessorGenerationCallbacks(
            string name,
            string path,
            string content
        )
        {
            foreach (var method in GetPostProcessorCallbacks(name))
            {
                var args = new[] { path, content };
                var returnValue = method.Invoke(null, args);
                if (method.ReturnType == typeof(string))
                {
                    // We want to chain content update between invocations
                    content = (string)returnValue;
                }
            }

            return content;
        }

        private static string OnGeneratedCSProject(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(
                nameof(OnGeneratedCSProject),
                path,
                content
            );
        }

        private static string OnGeneratedSlnSolution(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(
                nameof(OnGeneratedSlnSolution),
                path,
                content
            );
        }

        public void Sync()
        {
            SetupProjectSupportedExtensions();
            GenerateAndWriteSolutionAndProjects();

            OnGeneratedCSProjectFiles();
        }

        public bool SolutionExists()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        void SetupProjectSupportedExtensions()
        {
            m_ProjectSupportedExtensions = m_AssemblyNameProvider.ProjectSupportedExtensions;
        }

        bool ShouldFileBePartOfSolution(string file)
        {
            // Exclude files coming from packages except if they are internalized.
            return !m_AssemblyNameProvider.IsInternalizedPackagePath(file)
                && HasValidExtension(file);
        }

        bool HasValidExtension(string file)
        {
            string extension = Path.GetExtension(file);

            // Dll's are not scripts but still need to be included..
            if (extension == ".dll")
                return true;

            if (file.ToLower().EndsWith(".asmdef"))
                return true;

            return IsSupportedExtension(extension);
        }

        bool IsSupportedExtension(string extension)
        {
            extension = extension.TrimStart('.');
            if (k_BuiltinSupportedExtensions.ContainsKey(extension))
                return true;
            if (m_ProjectSupportedExtensions.Contains(extension))
                return true;
            return false;
        }

        static ScriptingLanguage ScriptingLanguageFor(Assembly assembly)
        {
            return ScriptingLanguageFor(GetExtensionOfSourceFiles(assembly.sourceFiles));
        }

        static string GetExtensionOfSourceFiles(string[] files)
        {
            return files.Length > 0 ? GetExtensionOfSourceFile(files[0]) : "NA";
        }

        static string GetExtensionOfSourceFile(string file)
        {
            var ext = Path.GetExtension(file).ToLower();
            ext = ext[1..]; //strip dot
            return ext;
        }

        static ScriptingLanguage ScriptingLanguageFor(string extension)
        {
            return k_BuiltinSupportedExtensions.TryGetValue(
                extension.TrimStart('.'),
                out var result
            )
                ? result
                : ScriptingLanguage.None;
        }

        public void GenerateAndWriteSolutionAndProjects()
        {
            // Only synchronize assemblies that have associated source files and ones that we actually want in the project.
            // This also filters out DLLs coming from .asmdef files in packages.
            var assemblies = m_AssemblyNameProvider
                .GetAssemblies(ShouldFileBePartOfSolution)
                .ToArray();

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            SyncSolution(assemblies);
            var allProjectAssemblies = RelevantAssembliesForMode(assemblies).ToList();
            foreach (Assembly assembly in allProjectAssemblies)
            {
                var responseFileData = ParseResponseFileData(assembly);
                SyncProject(assembly, allAssetProjectParts, responseFileData);
            }

            GenerateNugetJsonSourceFiles();
        }

        List<ResponseFileData> ParseResponseFileData(Assembly assembly)
        {
            var systemReferenceDirectories = CompilationPipeline.GetSystemAssemblyDirectories(
                assembly.compilerOptions.ApiCompatibilityLevel
            );

            Dictionary<string, ResponseFileData> responseFilesData =
                assembly.compilerOptions.ResponseFiles.ToDictionary(
                    x => x,
                    x =>
                        m_AssemblyNameProvider.ParseResponseFile(
                            x,
                            ProjectDirectory,
                            systemReferenceDirectories
                        )
                );

            Dictionary<string, ResponseFileData> responseFilesWithErrors = responseFilesData
                .Where(x => x.Value.Errors.Any())
                .ToDictionary(x => x.Key, x => x.Value);

            if (responseFilesWithErrors.Any())
            {
                foreach (var error in responseFilesWithErrors)
                    foreach (var valueError in error.Value.Errors)
                    {
                        Debug.LogError($"{error.Key} Parse Error : {valueError}");
                    }
            }

            return responseFilesData.Select(x => x.Value).ToList();
        }

        Dictionary<string, List<XElement>> GenerateAllAssetProjectParts()
        {
            Dictionary<string, List<XElement>> stringBuilders = new();
            foreach (string asset in m_AssemblyNameProvider.GetAllAssetPaths())
            {
                // Exclude files coming from packages except if they are internalized.
                // TODO: We need assets from the assembly API
                if (m_AssemblyNameProvider.IsInternalizedPackagePath(asset))
                {
                    continue;
                }

                string extension = Path.GetExtension(asset);
                if (
                    IsSupportedExtension(extension)
                    && ScriptingLanguage.None == ScriptingLanguageFor(extension)
                )
                {
                    // Find assembly the asset belongs to by adding script extension and using compilation pipeline.
                    var assemblyName = m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset);

                    if (string.IsNullOrEmpty(assemblyName))
                    {
                        continue;
                    }

                    assemblyName = Path.GetFileNameWithoutExtension(assemblyName);

                    if (!stringBuilders.TryGetValue(assemblyName, out var projectBuilder))
                    {
                        projectBuilder = new List<XElement>();
                        stringBuilders[assemblyName] = projectBuilder;
                    }

                    var noneElement = new XElement("None");

                    var fullFile = m_FileIOProvider.EscapedRelativePathFor(asset, ProjectDirectory);

                    fullFile = Path.Combine(ProjectDirectory, fullFile);
                    noneElement.SetAttributeValue("Include", fullFile);
                    projectBuilder.Add(noneElement);
                }
            }

            var result = new Dictionary<string, List<XElement>>();

            foreach (var entry in stringBuilders)
            {
                result[entry.Key] = entry.Value;
            }

            return result;
        }

        void SyncProject(
            Assembly assembly,
            Dictionary<string, List<XElement>> allAssetsProjectParts,
            List<ResponseFileData> responseFilesData
        )
        {
            SyncProjectFileIfNotChanged(
                ProjectFile(assembly),
                ProjectText(assembly, allAssetsProjectParts, responseFilesData)
            );
        }

        void SyncProjectFileIfNotChanged(string path, string newContents)
        {
            if (Path.GetExtension(path) == ".csproj")
            {
                newContents = OnGeneratedCSProject(path, newContents);
            }

            SyncFileIfNotChanged(path, newContents);
        }

        void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            newContents = OnGeneratedSlnSolution(path, newContents);

            SyncFileIfNotChanged(path, newContents);
        }

        void SyncFileIfNotChanged(string filename, string newContents)
        {
            try
            {
                if (
                    m_FileIOProvider.Exists(filename)
                    && newContents == m_FileIOProvider.ReadAllText(filename)
                )
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            m_FileIOProvider.WriteAllText(filename, newContents);
        }

        private const string SDKStyleCsProj =
            @"
        <Project Sdk=""Microsoft.NET.Sdk"">
        <PropertyGroup>
            <TargetFramework>netstandard2.1</TargetFramework>
            <DisableImplicitNamespaceImports>true</DisableImplicitNamespaceImports>
            <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
        </PropertyGroup>
        <PropertyGroup>
            <DefaultItemExcludes>$(DefaultItemExcludes);Library/;**/*.*</DefaultItemExcludes>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
        </PropertyGroup>
        </Project>
        ";

        string ProjectText(
            Assembly assembly,
            Dictionary<string, List<XElement>> allAssetsProjectParts,
            List<ResponseFileData> responseFilesData
        )
        {
            // We parse the sdk style project into an XML Document we can then add to :D
            var document = XDocument.Parse(SDKStyleCsProj);
            var project = document.Element("Project");
            var targetFrameWork = project.Elements().First().Element("TargetFramework");

            var targetGroup = BuildPipeline.GetBuildTargetGroup(
                EditorUserBuildSettings.activeBuildTarget
            );

            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(targetGroup);

            var netSettings = PlayerSettings.GetApiCompatibilityLevel(namedBuildTarget);

            targetFrameWork.Value = GetTargetFrameworkVersion(netSettings);

            var otherArguments = GetOtherArgumentsFromResponseFilesData(responseFilesData);

            AddCommonProperties(assembly, responseFilesData, project, otherArguments);

            // we have source files
            if (assembly.sourceFiles.Length != 0)
            {
                ReadOnlySpan<char> dirPathTemp = default;
                HashSet<string> extensions = new HashSet<string>();
                foreach(var path in assembly.sourceFiles)
                {
                    var dirName = Path.GetDirectoryName(path.AsSpan());
                    extensions.Add(Path.GetExtension(path));
                    if (dirPathTemp.IsEmpty || dirName.Length < dirPathTemp.Length)
                        dirPathTemp = dirName;
                }
                string dirPath = Path.Join(ProjectDirectory, m_FileIOProvider.EscapedRelativePathFor(dirPathTemp.ToString(), ProjectDirectory), $"**{Path.DirectorySeparatorChar}*");

                var itemGroup = new XElement("ItemGroup");
                foreach (var extension in extensions)
                {
                    itemGroup.Add(
                        new XElement("Compile", new XAttribute("Include", $"{dirPath}{extension}"))
                    );
                }

                /*
                foreach (var file in assembly.sourceFiles)
                {
                    // It should have the entire path to the source file
                    var fullFile = m_FileIOProvider.EscapedRelativePathFor(file, ProjectDirectory);

                    fullFile = Path.Combine(ProjectDirectory, fullFile);
                    itemGroup.Add(
                        new XElement("Compile", new XAttribute("Include", $"{fullFile}"))
                    );
                }
                */
                project.Add(itemGroup);
            }

            //  Append additional non-script files that should be included in project generation.
            if (
                allAssetsProjectParts.TryGetValue(assembly.name, out var additionalAssetsForProject)
            )
            {
                var itemGroup = new XElement("ItemGroup");
                itemGroup.Add(additionalAssetsForProject);
                project.Add(itemGroup);
            }

            var responseRefs = responseFilesData.SelectMany(
                x => x.FullPathReferences.Select(r => r)
            );
            var internalAssemblyReferences = assembly.assemblyReferences
                .Where(i => !i.sourceFiles.Any(ShouldFileBePartOfSolution))
                .Select(i => i.outputPath);
            var allReferences = assembly.compiledAssemblyReferences
                .Union(responseRefs)
                .Union(internalAssemblyReferences);

            if (allReferences.Any())
            {
                var refItemGroup = new XElement("ItemGroup");
                foreach (var reference in allReferences)
                {
                    string fullReference = Path.IsPathRooted(reference)
                        ? reference
                        : Path.Combine(ProjectDirectory, reference);
                    AppendReference(fullReference, refItemGroup, targetFrameWork.Value);
                }

                project.Add(refItemGroup);
            }

            if (assembly.assemblyReferences.Any())
            {
                var assemblyRefItemGroup = new XElement("ItemGroup");
                foreach (
                    Assembly reference in assembly.assemblyReferences.Where(
                        i => i.sourceFiles.Any(ShouldFileBePartOfSolution)
                    )
                )
                {
                    var packRefElement = new XElement(
                        "ProjectReference",
                        new XAttribute(
                            "Include",
                            // It should have the entire path to the project file
                            Path.Combine(
                                CSharpProjFoldersDirectory,
                                reference.name,
                                reference.name + GetProjectExtension()
                            )
                        ),
                        new XElement("Project", $"{ProjectGuid(reference.name)}"),
                        new XElement("Name", reference.name + GetProjectExtension())
                    );

                    assemblyRefItemGroup.Add(packRefElement);
                }

                project.Add(assemblyRefItemGroup);
            }

            {
                var analyzersRefItemGroup = new XElement("ItemGroup");

                var analyzers = RetrieveRoslynAnalyzers(assembly, otherArguments);
                foreach (var item in analyzers)
                {
                    analyzersRefItemGroup.Add(new XElement("Analyzer",
                        new XAttribute("Include", item)));
                }

                project.Add(analyzersRefItemGroup);
            }

            if (
                m_AssemblyNameProvider.ProjectGenerationFlag.HasFlag(
                    ProjectGenerationFlag.Analyzers
                ) || CheckIfAnalyzerIsAllowedOnCSProj(assembly)
            )
            {
                var analyzersRefItemGroup = new XElement("ItemGroup");

                analyzersRefItemGroup.Add(
                    AddNugetPackageReference("Microsoft.Unity.Analyzers", "*", true)
                );

                project.Add(analyzersRefItemGroup);
            }

            return document.ToString();
        }

        private bool CheckIfAnalyzerIsAllowedOnCSProj(Assembly assembly)
        {
            return assembly.sourceFiles.Any(
                x => x.StartsWith("Assets", StringComparison.InvariantCultureIgnoreCase)
            );
        }

        private XElement AddNugetPackageReference(string nugetPackageId, string nugetPackageVersion)
        {
            return new(
                "PackageReference",
                new XAttribute("Include", nugetPackageId),
                new XAttribute("Version", nugetPackageVersion)
            );
        }

        private XElement AddNugetPackageReference(
            string nugetPackageId,
            string nugetPackageVersion,
            bool isAnalyzer = false
        )
        {
            return new(
                "PackageReference",
                new XAttribute("Include", nugetPackageId),
                new XAttribute("Version", nugetPackageVersion),
                new XElement("PrivateAssets", "all"),
                new XElement("IncludeAssets", "runtime; build; native; contentfiles; analyzers")
            );
        }

        static void AppendReference(
            string fullReference,
            XElement projectBuilder,
            string targetFrameWork
        )
        {
            var escapedFullPath = SecurityElement.Escape(fullReference);
            escapedFullPath = escapedFullPath.NormalizePath();

            var reference = new XElement(
                "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(escapedFullPath))
            );

#if !UNITY_2023_1_OR_NEWER
            if (targetFrameWork.Contains("netstandard"))
                escapedFullPath = HandleEditorReference(escapedFullPath);
#endif

            var hintPath = new XElement("HintPath") { Value = escapedFullPath };
            reference.Add(hintPath);
            projectBuilder.Add(reference);
        }

#if !UNITY_2023_1_OR_NEWER
        /*
            This is a hack to get around the fact that the editor references a bunch of facades that are not in the netstandard2.0 or 2.1
            We need to replace the references with the ones that are in the netstandard2.0 or 2.1 compat folder
        */
        static string HandleEditorReference(string referencePath)
        {
            var facadesPath = "UnityReferenceAssemblies\\unity-4.8-api\\Facades\\";
            var referenceName = Path.GetFileNameWithoutExtension(referencePath);

            return referenceName switch
            {
                "Microsoft.Win32.Primitives"
                or "System.AppContext"
                or "System.Collections.Concurrent"
                or "System.Collections.NonGeneric"
                or "System.Collections.Specialized"
                or "System.ComponentModel"
                or "System.ComponentModel.EventBasedAsync"
                or "System.Diagnostics.Contracts"
                or "System.Diagnostics.Debug"
                or "System.Diagnostics.Tools"
                or "System.Diagnostics.Tracing"
                or "System.Globalization"
                or "System.Globalization.Calendars"
                or "System.IO"
                or "System.IO.Compression"
                or "System.IO.Compression.ZipFile"
                or "System.IO.FileSystem"
                or "System.IO.FileSystem.Primitives"
                or "System.Linq"
                or "System.Linq.Expressions"
                or "System.Net.Http"
                or "System.Net.Primitives"
                or "System.Net.Sockets"
                or "System.ObjectModel"
                or "System.Reflection"
                or "System.Reflection.Extensions"
                or "System.Reflection.Primitives"
                or "System.Resources.ResourceManager"
                or "System.Runtime"
                or "System.Runtime.Extensions"
                or "System.Runtime.Handles"
                or "System.Runtime.InteropServices"
                or "System.Runtime.InteropServices.RuntimeInformation"
                or "System.Runtime.Numerics"
                or "System.Security.Cryptography.Algorithms"
                or "System.Security.Cryptography.Encoding"
                or "System.Security.Cryptography.Primitives"
                or "System.Security.Cryptography.X509Certificates"
                or "System.Text.Encoding"
                or "System.Text.Encoding.Extensions"
                or "System.Text.RegularExpressions"
                or "System.Threading"
                or "System.Threading.Tasks"
                or "System.Threading.Tasks.Parallel"
                or "System.Threading.Thread"
                or "System.Threading.ThreadPool"
                or "System.Threading.Timer"
                or "System.ValueTuple"
                or "System.Xml.ReaderWriter"
                or "System.Xml.XDocument"
                or "System.Xml.XmlDocument"
                or "System.Xml.XmlSerializer"
                or "System.Xml.XPath"
                or "System.Xml.XPath.XDocument"
                    => referencePath.Replace(
                        facadesPath,
                        $"NetStandard\\compat\\2.1.0\\shims\\netstandard\\"
                    ),
                "System.Runtime.InteropServices.WindowsRuntime"
                    => referencePath.Replace(facadesPath, $"NetStandard\\Extensions\\2.0.0\\"),
                "netstandard" => referencePath.Replace(facadesPath, $"NetStandard\\2.1.0\\"),
                _ => referencePath.Replace(facadesPath, $"NetStandard\\compat\\2.1.0\\shims\\"),
            };
        }
#endif

        private void AddCommonProperties(
            Assembly assembly,
            List<ResponseFileData> responseFilesData,
            XElement builder,
            ILookup<string, string> otherArguments
        )
        {
            // Language version
            var langVersion = GenerateLangVersion(otherArguments["langversion"], assembly);

            var commonPropertyGroup = new XElement("PropertyGroup");
            var langElement = new XElement("LangVersion") { Value = langVersion };
            commonPropertyGroup.Add(langElement);

            // Allow unsafe code
            bool allowUnsafeCode =
                assembly.compilerOptions.AllowUnsafeCode | responseFilesData.Any(x => x.Unsafe);

            var unsafeElement = new XElement("AllowUnsafeBlocks")
            {
                Value = allowUnsafeCode.ToString()
            };
            commonPropertyGroup.Add(unsafeElement);

            var warningLevel = new XElement("WarningLevel", "4");
            commonPropertyGroup.Add(warningLevel);

            commonPropertyGroup.Add(new XElement("NoWarn", "USG0001"));

            var noStdLib = new XElement("NoStdLib", "true");
            commonPropertyGroup.Add(noStdLib);

            var assemblyNameElement = new XElement("AssemblyName", assembly.name);
            commonPropertyGroup.Add(assemblyNameElement);

            // we need to grab all the defines and add them to a property group
            var defines = string.Join(
                ";",
                new[] { "DEBUG", "TRACE" }
                    .Concat(assembly.defines)
                    .Concat(responseFilesData.SelectMany(x => x.Defines))
                    .Concat(EditorUserBuildSettings.activeScriptCompilationDefines)
                    .Distinct()
                    .ToArray()
            );
            var definePropertyGroup = new XElement("PropertyGroup");
            var definesElement = new XElement("DefineConstants") { Value = defines };
            definePropertyGroup.Add(definesElement);
            builder.Add(definePropertyGroup);

            var ruleSets = GenerateRoslynAnalyzerRulesetPath(assembly, otherArguments);

            if (ruleSets.Length != 0)
            {
                foreach (var item in ruleSets)
                {
                    var ruleElement = new XElement("CodeAnalysisRuleSet") { Value = item };
                    commonPropertyGroup.Add(ruleElement);
                }
            }

            builder.Add(commonPropertyGroup);
        }

        public string ProjectFile(Assembly assembly)
        {
            var fileBuilder = new StringBuilder(assembly.name);
            _ = fileBuilder.Append(".csproj");

            string csharpProjectFolderPath = Path.Combine(
                CSharpProjFoldersDirectory,
                assembly.name
            );

            if (!m_FileIOProvider.DirectoryExists(csharpProjectFolderPath))
            {
                m_FileIOProvider.CreateDirectory(csharpProjectFolderPath);
            }

            return Path.Combine(csharpProjectFolderPath, fileBuilder.ToString());
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
        }

        private static string GenerateLangVersion(
            IEnumerable<string> langVersionList,
            Assembly assembly
        )
        {
            var langVersion = langVersionList.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(langVersion)
                ? langVersion
                : assembly.compilerOptions.LanguageVersion;
        }

        private static string[] GenerateRoslynAnalyzerRulesetPath(
            Assembly assembly,
            ILookup<string, string> otherResponseFilesData
        )
        {
            return otherResponseFilesData["ruleset"]
                .Append(assembly.compilerOptions.RoslynAnalyzerRulesetPath)
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .Select(x => MakeAbsolutePath(x).NormalizePath())
                .ToArray();
        }

        string[] RetrieveRoslynAnalyzers(Assembly assembly, ILookup<string, string> otherArguments)
        {
            return otherArguments["analyzer"].Concat(otherArguments["a"])
                .SelectMany(x => x.Split(';'))
                .Concat(assembly.compilerOptions.RoslynAnalyzerDllPaths)
                .Select(MakeAbsolutePath)
                .Distinct()
                .ToArray();
        }

        private static string MakeAbsolutePath(string path)
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        }

        private static ILookup<string, string> GetOtherArgumentsFromResponseFilesData(
            List<ResponseFileData> responseFilesData
        )
        {
            var paths = responseFilesData
                .SelectMany(x =>
                {
                    return x.OtherArguments
                        .Where(a => a.StartsWith("/") || a.StartsWith("-"))
                        .Select(b =>
                        {
                            var index = b.IndexOf(":", StringComparison.Ordinal);
                            if (index > 0 && b.Length > index)
                            {
                                var key = b[1..index];
                                return new KeyValuePair<string, string>(key, b[(index + 1)..]);
                            }

                            const string warnAsError = "warnaserror";
                            return b[1..].StartsWith(warnAsError)
                                ? new KeyValuePair<string, string>(
                                    warnAsError,
                                    b[(warnAsError.Length + 1)..]
                                )
                                : default;
                        });
                })
                .Distinct()
                .ToLookup(o => o.Key, pair => pair.Value);
            return paths;
        }

        static string GetSolutionText()
        {
            return string.Join(
                    "\r\n",
                    "",
                    "Microsoft Visual Studio Solution File, Format Version {0}",
                    "# Visual Studio {1}",
                    "{2}",
                    "Global",
                    "    GlobalSection(SolutionConfigurationPlatforms) = preSolution",
                    "        Debug|Any CPU = Debug|Any CPU",
                    "    EndGlobalSection",
                    "    GlobalSection(ProjectConfigurationPlatforms) = postSolution",
                    "{3}",
                    "    EndGlobalSection",
                    "    GlobalSection(SolutionProperties) = preSolution",
                    "        HideSolutionNode = FALSE",
                    "    EndGlobalSection",
                    "EndGlobal",
                    ""
                )
                .Replace("    ", "\t");
        }

        private static string GetTargetFrameworkVersion(ApiCompatibilityLevel netSettings)
        {
            return netSettings switch
            {
                ApiCompatibilityLevel.NET_2_0
                or ApiCompatibilityLevel.NET_2_0_Subset
                or ApiCompatibilityLevel.NET_Web
                or ApiCompatibilityLevel.NET_Micro
                    => k_TargetFrameworkVersion,
                ApiCompatibilityLevel.NET_Standard => "netstandard2.1",
                ApiCompatibilityLevel.NET_Unity_4_8 => k_TargetFrameworkVersion,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        void SyncSolution(IEnumerable<Assembly> assemblies)
        {
            SyncSolutionFileIfNotChanged(SolutionFile(), SolutionText(assemblies));
        }

        string SolutionText(IEnumerable<Assembly> assemblies)
        {
            var fileVersion = "11.00";
            var vsVersion = "2022";

            var relevantAssemblies = RelevantAssembliesForMode(assemblies);
            string projectEntries = GetProjectEntries(relevantAssemblies);
            string projectConfigurations = string.Join(
                k_WindowsNewline,
                relevantAssemblies
                    .Select(i => GetProjectActiveConfigurations(ProjectGuid(i.name)))
                    .ToArray()
            );
            return string.Format(
                GetSolutionText(),
                fileVersion,
                vsVersion,
                projectEntries,
                projectConfigurations
            );
        }

        static IEnumerable<Assembly> RelevantAssembliesForMode(IEnumerable<Assembly> assemblies)
        {
            return assemblies.Where(i => ScriptingLanguage.CSharp == ScriptingLanguageFor(i));
        }

        /// <summary>
        /// Get a Project("{guid}") = "MyProject", "{m_TargetCSharpProjFolders}/{projectFileName}/MyProject.csproj", "{projectGuid}"
        /// /// entry for each relevant language
        /// </summary>
        string GetProjectEntries(IEnumerable<Assembly> assemblies)
        {
            var projectEntries = assemblies.Select(i =>
            {
                var projectName = Path.GetFileName(ProjectFile(i));

                var projectFileName = projectName[..^GetProjectExtension().Length];

                return string.Format(
                    m_SolutionProjectEntryTemplate,
                    SolutionGuid(i),
                    i.name,
                    $"{m_TargetCSharpProjFolders}/{projectFileName}/{projectName}",
                    ProjectGuid(i.name)
                );
            });

            return string.Join(k_WindowsNewline, projectEntries.ToArray());
        }

        /// <summary>
        /// Generate the active configuration string for a given project guid
        /// </summary>
        string GetProjectActiveConfigurations(string projectGuid)
        {
            return string.Format(m_SolutionProjectConfigurationTemplate, projectGuid);
        }

        string ProjectGuid(string assembly)
        {
            return m_GUIDProvider.ProjectGuid(m_ProjectName, assembly);
        }

        string SolutionGuid(Assembly assembly)
        {
            return m_GUIDProvider.SolutionGuid(
                m_ProjectName,
                GetExtensionOfSourceFiles(assembly.sourceFiles)
            );
        }

        static string GetProjectExtension()
        {
            return ".csproj";
        }

        void GenerateNugetJsonSourceFiles()
        {
            string dotnetCommand = GetDotnetCommand();

            if (dotnetCommand == null)
            {
                Debug.Log(
                    "Could not find a compatible dotnet command. Aborting Nuget Json generation."
                );
                return;
            }

            string dotnetArguments = GetDotnetArguments();

            if (dotnetArguments == null)
            {
                Debug.Log(
                    "Could not find a compatible dotnet arguments. Aborting Nuget Json generation."
                );
                return;
            }

            using var process = new System.Diagnostics.Process();
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dotnetCommand,
                Arguments = dotnetArguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo = processStartInfo;
            process.Start();
            process.WaitForExit();
        }

        string GetDotnetCommand()
        {
#if UNITY_EDITOR_WIN
            return "dotnet";
#elif UNITY_EDITOR_LINUX
            return "/bin/bash";
#elif UNITY_EDITOR_OSX
            return "/bin/zsh";
#else
            return null;
#endif
        }

        string GetDotnetArguments()
        {
#if UNITY_EDITOR_WIN
            return "build";
#elif UNITY_EDITOR_LINUX || UNITY_EDITOR_OSX
            return "-c \"dotnet build\"";
#else
            return null;
#endif
        }
    }

    public static class SolutionGuidGenerator
    {
        static readonly MD5 mD5 = MD5CryptoServiceProvider.Create();

        public static string GuidForProject(string projectName)
        {
            return ComputeGuidHashFor(projectName + "salt");
        }

        public static string GuidForSolution(string projectName, string sourceFileExtension)
        {
            return "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
        }

        static string ComputeGuidHashFor(string input)
        {
            var hash = mD5.ComputeHash(Encoding.Default.GetBytes(input));
            return new Guid(hash).ToString();
        }
    }
}
