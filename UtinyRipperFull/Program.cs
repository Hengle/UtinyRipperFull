﻿#if DEBUG
#define DEBUG_PROGRAM
#endif

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using UtinyRipper;
using UtinyRipper.AssetExporters;
using UtinyRipper.Classes;
using UtinyRipperFull.Exporters;

using Object = UtinyRipper.Classes.Object;
using Version = UtinyRipper.Version;

namespace UtinyRipperFull
{
	public class Program
	{
		public static IEnumerable<Object> FetchExportObjects(FileCollection collection)
		{
			//yield break;
			return collection.FetchAssets();
		}

		public static void Main(string[] args)
		{
			Logger.Instance = ConsoleLogger.Instance;
			Config.IsAdvancedLog = true;
			Config.IsGenerateGUIDByContent = false;
			Config.IsExportDependencies = false;
			Config.IsConvertTexturesToPNG = true;

			if (args.Length == 0)
			{
				Console.WriteLine("No arguments");
				return;
			}
			foreach (string arg in args)
			{
				if (!FileMultiStream.Exists(arg))
				{
					Console.WriteLine(FileMultiStream.IsMultiFile(arg) ?
						$"File {arg} doen't has all parts for combining" :
						$"File {arg} doesn't exist", arg);
					return;
				}
			}

			Program program = new Program();
			program.Load(args);
			Console.ReadKey();
		}

		public Program()
		{
			m_collection = new FileCollection(OnRequestDependency, OnRequestAssembly);
		}

		public void Load(IReadOnlyList<string> args)
		{
#if !DEBUG_PROGRAM
			try
#endif
			{
				string name = Path.GetFileNameWithoutExtension(args.First());
				string exportPath = Path.Combine("Ripped", name);

				Prepare(exportPath, args);
				LoadAssemblies();
				LoadFiles(args);
				Validate();

				m_collection.Exporter.Export(exportPath, FetchExportObjects(m_collection));
				Logger.Instance.Log(LogType.Info, LogCategory.General, "Finished");
			}
#if !DEBUG_PROGRAM
			catch (Exception ex)
			{
				Logger.Instance.Log(LogType.Error, LogCategory.General, ex.ToString());
			}
#endif
		}

		private void Prepare(string exportPath, IEnumerable<string> filePathes)
		{
			PrepareExportDirectory(exportPath);

			foreach (string filePath in filePathes)
			{
				string dirPath = Path.GetDirectoryName(filePath);
				m_knownDirectories.Add(dirPath);
			}
			
			TextureAssetExporter textureExporter = new TextureAssetExporter();
			m_collection.Exporter.OverrideExporter(ClassIDType.Texture2D, textureExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Cubemap, textureExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Sprite, textureExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Shader, new ShaderAssetExporter());
			m_collection.Exporter.OverrideExporter(ClassIDType.AudioClip, new AudioAssetExporter());

			EngineAssetExporter engineExporter = new EngineAssetExporter();
			m_collection.Exporter.OverrideExporter(ClassIDType.Material, engineExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Texture2D, engineExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Mesh, engineExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Shader, engineExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Font, engineExporter);
			m_collection.Exporter.OverrideExporter(ClassIDType.Sprite, engineExporter);
		}

		private void LoadAssemblies()
		{
			foreach (string dirPath in m_knownDirectories)
			{
				string path = Path.Combine(dirPath, AssemblyFolder);
				DirectoryInfo managedDirecoty = new DirectoryInfo(path);
				if (!managedDirecoty.Exists)
				{
					continue;
				}

				foreach (FileInfo file in managedDirecoty.EnumerateFiles())
				{
					if (AssemblyManager.IsAssembly(file.Name))
					{
						LoadAssembly(file.FullName);
					}
				}
				break;
			}
		}

		private void LoadFiles(IEnumerable<string> filePathes)
		{
			foreach (string filePath in filePathes)
			{
				string fileName = FileMultiStream.GetFileName(filePath);
				LoadFile(filePath, fileName);
			}
		}

		private void LoadFile(string fullFilePath, string originalFileName)
		{
			if (m_knownFiles.Add(originalFileName))
			{
				string filePath = FileMultiStream.GetFilePath(fullFilePath);
				using (Stream stream = FileMultiStream.OpenRead(filePath))
				{
					m_collection.Read(stream, filePath, originalFileName);
				}
			}
		}

		private void LoadAssembly(string filePath)
		{
			if (m_knownAssemblies.Add(filePath))
			{
				using (Stream stream = FileMultiStream.OpenRead(filePath))
				{
					m_collection.ReadAssembly(stream, filePath);
				}
			}
		}

		private void Validate()
		{
			Version[] versions = m_collection.Files.Select(t => t.Version).Distinct().ToArray();
			if (versions.Count() > 1)
			{
				Logger.Instance.Log(LogType.Warning, LogCategory.Import, $"Asset collection has versions probably incompatible with each other. Here they are:");
				foreach (Version version in versions)
				{
					Logger.Instance.Log(LogType.Warning, LogCategory.Import, version.ToString());
				}
			}
		}

		private void LoadDependency(string fileName)
		{
			foreach (string loadName in FetchNameVariants(fileName))
			{
				bool found = TryLoadDependency(loadName, fileName);
				if (found)
				{
					return;
				}
			}

			Logger.Instance.Log(LogType.Warning, LogCategory.Import, $"Dependency '{fileName}' wasn't found");
			m_knownFiles.Add(fileName);
		}

		private void LoadDependencyAssembly(string fileName)
		{
			bool found = TryLoadDependencyAssembly(fileName);
			if (found)
			{
				return;
			}

			Logger.Instance.Log(LogType.Warning, LogCategory.Import, $"Assembly '{fileName}' wasn't found");
			m_knownAssemblies.Add(fileName);
		}

		private bool TryLoadDependency(string loadName, string originalName)
		{
			foreach (string dirPath in m_knownDirectories)
			{
				string path = Path.Combine(dirPath, loadName);
				if (FileMultiStream.Exists(path))
				{
					LoadFile(path, originalName);
					Logger.Instance.Log(LogType.Info, LogCategory.Import, $"Dependency '{path}' was loaded");
					return true;
				}
			}
			return false;
		}

		private bool TryLoadDependencyAssembly(string loadName)
		{
			foreach (string dirPath in m_knownDirectories)
			{
				string path = Path.Combine(dirPath, AssemblyFolder);
				DirectoryInfo managedDirecoty = new DirectoryInfo(path);
				if (!managedDirecoty.Exists)
				{
					continue;
				}

				foreach (FileInfo file in managedDirecoty.EnumerateFiles())
				{
					if (AssemblyManager.IsAssembly(file.Name))
					{
						string fileName = file.Name;
						if (fileName == loadName)
						{
							LoadAssembly(file.FullName);
							return true;
						}

						fileName = Path.GetFileNameWithoutExtension(fileName);
						if (fileName == loadName)
						{
							LoadAssembly(file.FullName);
							return true;
						}
					}
				}
				break;
			}
			return false;
		}

		private string FindScriptFolder()
		{
			const string ScriptFolderName = "Managed";
			foreach (string dirPath in m_knownDirectories)
			{
				string scriptPath = Path.Combine(dirPath, ScriptFolderName);
				if (Directory.Exists(scriptPath))
				{
					return scriptPath;
				}
			}
			return null;
		}

		private void OnRequestDependency(string dependency)
		{
			if (m_knownFiles.Contains(dependency))
			{
				return;
			}

			LoadDependency(dependency);
		}

		private void OnRequestAssembly(string assembly)
		{
			if (m_knownAssemblies.Contains(assembly))
			{
				return;
			}

			LoadDependencyAssembly(assembly);
		}

		private static void PrepareExportDirectory(string path)
		{
			string directory = Directory.GetCurrentDirectory();
			CheckWritePermission(directory);

			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}
		}

		private static void CheckWritePermission(string path)
		{
			WindowsIdentity identity = WindowsIdentity.GetCurrent();
			WindowsPrincipal principal = new WindowsPrincipal(identity);
			bool isInRoleWithAccess = false;
			try
			{
				DirectoryInfo di = new DirectoryInfo(path);
				DirectorySecurity ds = di.GetAccessControl();
				AuthorizationRuleCollection rules = ds.GetAccessRules(true, true, typeof(NTAccount));

				foreach (AuthorizationRule rule in rules)
				{
					FileSystemAccessRule fsAccessRule = rule as FileSystemAccessRule;
					if (fsAccessRule == null)
					{
						continue;
					}

					if ((fsAccessRule.FileSystemRights & FileSystemRights.Write) != 0)
					{
						NTAccount ntAccount = rule.IdentityReference as NTAccount;
						if (ntAccount == null)
						{
							continue;
						}

						if (principal.IsInRole(ntAccount.Value))
						{
							if (fsAccessRule.AccessControlType == AccessControlType.Deny)
							{
								isInRoleWithAccess = false;
								break;
							}
							isInRoleWithAccess = true;
						}
					}
				}
			}
			catch (UnauthorizedAccessException)
			{
			}

			if (!isInRoleWithAccess)
			{
				// is run as administrator?
				if (principal.IsInRole(WindowsBuiltInRole.Administrator))
				{
					return;
				}

				// try run as admin
				Process proc = new Process();
				string[] args = Environment.GetCommandLineArgs();
				proc.StartInfo.FileName = args[0];
				proc.StartInfo.Arguments = string.Join(" ", args.Skip(1).Select(t => $"\"{t}\""));
				proc.StartInfo.UseShellExecute = true;
				proc.StartInfo.Verb = "runas";

				try
				{
					proc.Start();
					Environment.Exit(0);
				}
				catch (Win32Exception ex)
				{
					//The operation was canceled by the user.
					const int ERROR_CANCELLED = 1223;
					if (ex.NativeErrorCode == ERROR_CANCELLED)
					{
						Logger.Instance.Log(LogType.Error, LogCategory.General, $"You can't export to folder {path} without Administrator permission");
						Console.ReadKey();
					}
					else
					{
						Logger.Instance.Log(LogType.Error, LogCategory.General, $"You have to restart application as Administator in order to export to folder {path}");
						Console.ReadKey();
					}
				}
			}
		}

		private static IEnumerable<string> FetchNameVariants(string name)
		{
			yield return name;

			const string libraryFolder = "library";
			if (name.ToLower().StartsWith(libraryFolder))
			{
				string fixedName = name.Substring(libraryFolder.Length + 1);
				yield return fixedName;

				fixedName = Path.Combine("Resources", fixedName);
				yield return fixedName;
			}
		}

		private readonly HashSet<string> m_knownDirectories = new HashSet<string>();
		private readonly HashSet<string> m_knownFiles = new HashSet<string>();
		private readonly HashSet<string> m_knownAssemblies = new HashSet<string>();

		private const string AssemblyFolder = "Managed";

		private readonly FileCollection m_collection;
	}
}