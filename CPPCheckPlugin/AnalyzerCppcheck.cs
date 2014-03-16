﻿using System;
using System.Collections.Generic;
using System.Linq;
using EnvDTE;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Text.RegularExpressions;

namespace VSPackage.CPPCheckPlugin
{
	class AnalyzerCppcheck : ICodeAnalyzer
	{
		public override void analyze(List<SourceFile> filesToAnalyze, OutputWindowPane outputWindow, bool is64bitConfiguration,
			bool isDebugConfiguration, bool analysisOnSavedFile)
		{
			if (filesToAnalyze.Count == 0)
				return;

			Debug.Assert(_numCores > 0);
			String cppheckargs = Properties.Settings.Default.DefaultArguments;

			if (Properties.Settings.Default.SeveritiesString.Length != 0)
				cppheckargs += " --enable=" + Properties.Settings.Default.SeveritiesString;

			HashSet<string> suppressions = new HashSet<string>(Properties.Settings.Default.SuppressionsString.Split(','));
			suppressions.Add("unmatchedSuppression");

			HashSet<string> skippedFilesMask = new HashSet<string>();
			HashSet<string> skippedIncludeMask = new HashSet<string>();

			SuppressionsInfo unitedSuppressionsInfo = readSuppressions(ICodeAnalyzer.SuppressionStorage.Global);
			unitedSuppressionsInfo.UnionWith(readSuppressions(ICodeAnalyzer.SuppressionStorage.Solution));

			// Creating the list of all different project locations (no duplicates)
			HashSet<string> projectPaths = new HashSet<string>(); // enforce uniqueness on the list of project paths
			foreach (var file in filesToAnalyze)
			{
				projectPaths.Add(file.BaseProjectPath);
			}

			Debug.Assert(projectPaths.Count == 1);
			_projectBasePath = projectPaths.First();
			_projectName = filesToAnalyze[0].ProjectName;

			// Creating the list of all different suppressions (no duplicates)
			foreach (var path in projectPaths)
			{
				unitedSuppressionsInfo.UnionWith(readSuppressions(SuppressionStorage.Project, path, filesToAnalyze[0].ProjectName));
			}

			cppheckargs += (" --relative-paths=\"" + filesToAnalyze[0].BaseProjectPath + "\"");
			cppheckargs += (" -j " + _numCores.ToString());
			if (Properties.Settings.Default.InconclusiveChecksEnabled)
				cppheckargs += " --inconclusive ";

			suppressions.UnionWith(unitedSuppressionsInfo.SuppressionLines);
			foreach (string suppression in suppressions)
			{
				if (!String.IsNullOrWhiteSpace(suppression))
					cppheckargs += (" --suppress=" + suppression);
			}

			// We only add include paths once, and then specify a set of files to check
			HashSet<string> includePaths = new HashSet<string>();
			foreach (var file in filesToAnalyze)
			{
				if (!MatchMasksList(file.FilePath, unitedSuppressionsInfo.SkippedFilesMask))
					includePaths.UnionWith(file.IncludePaths);
			}

			includePaths.Add(filesToAnalyze[0].BaseProjectPath); // Fix for #60

			foreach (string path in includePaths)
			{
				if (!MatchMasksList(path, unitedSuppressionsInfo.SkippedIncludesMask))
				{
					String includeArgument = " -I\"" + path + "\"";
					cppheckargs = cppheckargs + " " + includeArgument;
				}
			}

			foreach (SourceFile file in filesToAnalyze)
			{
				cppheckargs += " \"" + file.FilePath + "\"";
			}

			if ((analysisOnSavedFile && Properties.Settings.Default.FileOnlyCheckCurrentConfig) ||
				(!analysisOnSavedFile && Properties.Settings.Default.ProjectOnlyCheckCurrentConfig)) // Only checking current macros configuration (for speed)
			{
				cppheckargs = cppheckargs.Replace("--force", "");
				// Creating the list of all different macros (no duplicates)
				HashSet<string> macros = new HashSet<string>();
				macros.Add("__cplusplus=199711L"); // At least in VS2012, this is still 199711L
				// Assuming all files passed here are from the same project / same toolset, which should be true, so peeking the first file for global settings
				switch (filesToAnalyze[0].vcCompilerVersion)
				{
					case SourceFile.VCCompilerVersion.vc2003:
						macros.Add("_MSC_VER=1310");
						break;
					case SourceFile.VCCompilerVersion.vc2005:
						macros.Add("_MSC_VER=1400");
						break;
					case SourceFile.VCCompilerVersion.vc2008:
						macros.Add("_MSC_VER=1500");
						break;
					case SourceFile.VCCompilerVersion.vc2010:
						macros.Add("_MSC_VER=1600");
						break;
					case SourceFile.VCCompilerVersion.vc2012:
						macros.Add("_MSC_VER=1700");
						break;
					case SourceFile.VCCompilerVersion.vc2013:
						macros.Add("_MSC_VER=1800");
						break;
					case SourceFile.VCCompilerVersion.vcFuture:
						macros.Add("_MSC_VER=1900");
						break;
				}

				foreach (var file in filesToAnalyze)
				{
					macros.UnionWith(file.Macros);
				}
				macros.Add("WIN32");
				macros.Add("_WIN32");

				if (is64bitConfiguration)
				{
					macros.Add("_M_X64");
					macros.Add("_WIN64");
				}
				else
				{
					macros.Add("_M_IX86");
				}

				if (isDebugConfiguration)
					macros.Add("_DEBUG");

				foreach (string macro in macros)
				{
					if (!String.IsNullOrEmpty(macro) && !macro.Contains(" ") /* macros with spaces are invalid in VS */)
					{
						String macroArgument = " -D" + macro;
						cppheckargs += macroArgument;
					}
				}

				HashSet<string> macrosToUndefine = new HashSet<string>();
				foreach (var file in filesToAnalyze)
				{
					macrosToUndefine.UnionWith(file.MacrosToUndefine);
				}

				foreach (string macro in macrosToUndefine)
				{
					if (!String.IsNullOrEmpty(macro) && !macro.Contains(" ") /* macros with spaces are invalid in VS */)
					{
						String macroUndefArgument = " -U" + macro;
						cppheckargs += macroUndefArgument;
					}
				}
			}
			else if (!cppheckargs.Contains("--force"))
				cppheckargs += " --force";

			string analyzerPath = Properties.Settings.Default.CPPcheckPath;
			while (!File.Exists(analyzerPath))
			{
				OpenFileDialog dialog = new OpenFileDialog();
				dialog.Filter = "cppcheck executable|cppcheck.exe";
				if (dialog.ShowDialog() != DialogResult.OK)
					return;

				analyzerPath = dialog.FileName;
			}

			Properties.Settings.Default["CPPcheckPath"] = analyzerPath;
			Properties.Settings.Default.Save();
			run(analyzerPath, cppheckargs, outputWindow);
		}

		private static bool MatchMasksList(string line, HashSet<string> masks)
		{
			foreach (var mask in masks)
			{
				Regex rgx = new Regex(mask);
				if (rgx.IsMatch(line))
					return true;
			}
			return false;
		}

		public override void suppressProblem(Problem p, SuppressionScope scope)
		{
			if (p == null)
				return;
			if (!Directory.Exists(p.BaseProjectPath))
			{
				Debug.WriteLine("Error: directory " + p.BaseProjectPath + " doesn't exist, can't add suppression");
				return;
			}

			String simpleFileName = p.FileName;

			String suppressionLine = null;
			switch (scope)
			{
				case SuppressionScope.suppressAllMessagesThisFileGlobally:
					suppressionLine = "*:" + simpleFileName;
					break;
				case SuppressionScope.suppressAllMessagesThisFileSolutionWide:
					suppressionLine = "*:" + simpleFileName;
					break;
				case SuppressionScope.suppressAllMessagesThisFileProjectWide:
					suppressionLine = "*:" + simpleFileName;
					break;
				case SuppressionScope.suppressThisMessage:
					suppressionLine = p.MessageId + ":" + simpleFileName + ":" + p.Line;
					break;
				case SuppressionScope.suppressThisTypeOfMessageFileWide:
					suppressionLine = p.MessageId + ":" + simpleFileName;
					break;
				case SuppressionScope.suppressThisTypeOfMessagesGlobally:
					suppressionLine = p.MessageId;
					break;
				case SuppressionScope.suppressThisTypeOfMessageProjectWide:
					suppressionLine = p.MessageId;
					break;
				case SuppressionScope.suppressThisMessageSolutionWide:
					suppressionLine = p.MessageId + ":" + simpleFileName + ":" + p.Line;
					break;
				case SuppressionScope.suppressThisMessageGlobally:
					suppressionLine = p.MessageId + ":" + simpleFileName + ":" + p.Line;
					break;
				case SuppressionScope.suppressThisTypeOfMessagesSolutionWide:
					suppressionLine = p.MessageId;
					break;
				default:
					throw new InvalidOperationException("Unsupported value: " + scope.ToString());
			}

			String suppresionsFilePath = suppressionsFilePathByScope(scope, p.BaseProjectPath, p.ProjectName);
			Debug.Assert(suppresionsFilePath != null);

			SuppressionsInfo suppressionsInfo = new SuppressionsInfo();
			suppressionsInfo.LoadFromFile(suppresionsFilePath);

			suppressionsInfo.AddSuppressionLine(suppressionLine);

			suppressionsInfo.SaveToFile(suppresionsFilePath);
		}

		protected override SuppressionsInfo readSuppressions(SuppressionStorage storage, string projectBasePath = null, string projectName = null)
		{
			SuppressionsInfo suppressionsInfo = new SuppressionsInfo();

			String suppresionsFilePath = suppressionsFilePathByStorage(storage, projectBasePath, projectName);
			Debug.Assert(suppresionsFilePath != null);

			suppressionsInfo.LoadFromFile(suppresionsFilePath);

			return suppressionsInfo;
		}

		protected override List<Problem> parseOutput(String output)
		{
			// template={file}|{line}|{severity}|{id}|{message}

			List<Problem> list = new List<Problem>();

			if (String.IsNullOrWhiteSpace(output) || !output.Contains("|"))
				return list;

			String[] parsed = output.Split('|');
			if (parsed.Length != 5)
				return list;

			Problem.SeverityLevel severity = Problem.SeverityLevel.info;
			if (parsed[2] == "error")
				severity = Problem.SeverityLevel.error;
			else if (parsed[2] == "warning")
				severity = Problem.SeverityLevel.warning;

			list.Add(new Problem(this, severity, parsed[3], parsed[4], parsed[0], String.IsNullOrWhiteSpace(parsed[1]) ? 0 : Int32.Parse(parsed[1]), _projectBasePath, _projectName));
			return list;
		}
	}
}
