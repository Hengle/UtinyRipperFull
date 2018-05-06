﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using UtinyRipper;
using UtinyRipper.SerializedFiles;
using UtinyRipperFull.Exporters;

using Object = UtinyRipper.Classes.Object;
using Version = UtinyRipper.Version;

namespace UtinyRipperFull
{
	public static class Program
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
				Console.ReadKey();
				return;
			}
			foreach(string arg in args)
			{
				if(!FileMultiStream.Exists(arg))
				{
					Console.WriteLine(FileMultiStream.IsMultiFile(arg) ?
						$"File {arg} doen't has all parts for combining" :
						$"File {arg} doesn't exist", arg);
					Console.ReadKey();
					return;
				}
			}

			try
			{
				string name = Path.GetFileNameWithoutExtension(args.First());
				string exportPath = ".\\Ripped\\" + name;
				PrepareExportDirectory(exportPath);

				FileCollection collection = new FileCollection();
				collection.Exporter.OverrideExporter(ClassIDType.AudioClip, new AudioAssetExporter());
				collection.Exporter.OverrideExporter(ClassIDType.Cubemap, new TextureAssetExporter());
				collection.Exporter.OverrideExporter(ClassIDType.Texture2D, new TextureAssetExporter());

				LoadFiles(collection, args);

				LoadDependencies(collection, args);
				ValidateCollection(collection);

				collection.Exporter.Export(exportPath, FetchExportObjects(collection));

				Logger.Instance.Log(LogType.Info, LogCategory.General, "Finished");
			}
			catch(Exception ex)
			{
				Logger.Instance.Log(LogType.Error, LogCategory.General, ex.ToString());
			}
			Console.ReadKey();
		}

		private static void LoadFiles(FileCollection collection, IEnumerable<string> filePathes)
		{
			List<string> processed = new List<string>();
			foreach (string path in filePathes)
			{
				string filePath = FileMultiStream.GetFilePath(path);
				if (processed.Contains(filePath))
				{
					continue;
				}
				
				string fileName = FileMultiStream.GetFileName(path);
				using (Stream stream = FileMultiStream.OpenRead(path))
				{
					collection.Read(stream, filePath, fileName);
				}
				processed.Add(filePath);
			}
		}

		private static void ValidateCollection(FileCollection collection)
		{
			Version[] versions = collection.Files.Select(t => t.Version).Distinct().ToArray();
			if(versions.Count() > 1)
			{
				Logger.Instance.Log(LogType.Warning, LogCategory.Import, "Asset collection (probably) has incompatible with each assets file versions. Here they are:");
				foreach(Version version in versions)
				{
					Logger.Instance.Log(LogType.Warning, LogCategory.Import, version.ToString());
				}
			}
		}

		private static void LoadDependencies(FileCollection collection, IEnumerable<string> files)
		{
			HashSet<string> directories = new HashSet<string>();
			foreach (string filePath in files)
			{
				string dirPath = Path.GetDirectoryName(filePath);
				directories.Add(dirPath);
			}

			HashSet<string> processed = new HashSet<string>();
			foreach (ISerializedFile file in collection.Files)
			{
				processed.Add(file.Name);
			}

			for (int i = 0; i < collection.Files.Count; i++)
			{
				ISerializedFile serializedFile = collection.Files[i];
				foreach (FileIdentifier file in serializedFile.Dependencies)
				{
					string fileName = file.FilePath;
					if (processed.Contains(fileName))
					{
						continue;
					}
					
					LoadDependency(collection, directories, fileName);
					processed.Add(fileName);
				}
			}
		}

		private static void LoadDependency(FileCollection collection, IReadOnlyCollection<string> directories, string fileName)
		{
			foreach (string loadName in FetchNameVariants(fileName))
			{
				bool found = TryLoadDependency(collection, directories, fileName, loadName);
				if (found)
				{
					return;
				}
			}

			Logger.Instance.Log(LogType.Warning, LogCategory.Import, $"Dependency '{fileName}' wasn't found");
		}

		private static bool TryLoadDependency(FileCollection collection, IEnumerable<string> directories, string originalName, string loadName)
		{
			foreach (string dirPath in directories)
			{
				string path = Path.Combine(dirPath, loadName);
				try
				{
					if (FileMultiStream.Exists(path))
					{
						using (Stream stream = FileMultiStream.OpenRead(path))
						{
							collection.Read(stream, path, originalName);
						}

						Logger.Instance.Log(LogType.Info, LogCategory.Import, $"Dependency '{path}' was loaded");
						return true;
					}
				}
				catch (Exception ex)
				{
					Logger.Instance.Log(LogType.Error, LogCategory.Import, $"Can't parse dependency file {path}");
					Logger.Instance.Log(LogType.Error, LogCategory.Debug, ex.ToString());
				}
			}
			return false;
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
	}
}
