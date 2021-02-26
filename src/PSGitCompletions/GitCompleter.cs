﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PSGitCompletionsTests")]

namespace PowerCode
{

    public class GitCompleter : IModuleAssemblyInitializer
    {
        public void OnImport()
        {
            using var ps = PowerShell.Create(runspace: RunspaceMode.CurrentRunspace);
            ps.AddCommand("Register-ArgumentCompleter")
                .AddParameter("-Native", true)
                .AddParameter("CommandName", "git")
                .AddParameter("ScriptBlock",
                    ScriptBlock.Create("param($wordToComplete, $ast, $cursorPosition) [PowerCode.GitCompleter]::CompleteInput($wordToComplete, $ast, $cursorPosition)"));
            ps.Invoke();
        }

        public static IList<CompletionResult> CompleteInput(string wordToComplete, CommandAst ast, int cursorPosition)
        {
            var completeCommandParameters = CompleteCommandParameters.Create(wordToComplete, ast, cursorPosition);
            if (completeCommandParameters.IsCompletingCommand) {
                return CompleteGitCommands(wordToComplete: wordToComplete);
            }
            return CompleteGitCommand(completeCommandParameters);
        }


        private static IList<CompletionResult> CompleteGitCommand(CompleteCommandParameters completeCommandParameters) {
            var wordToComplete = completeCommandParameters.WordToComplete;
            bool isCompletingParameterName = completeCommandParameters.IsCompletingParameterName;
            switch (completeCommandParameters.CommandName)
            {
                case "add":
                    if (!isCompletingParameterName) return CompleteModifiedFiles(wordToComplete: wordToComplete);
                    goto default;
                case "branch":
                    if (!isCompletingParameterName) return CompleteBranches(wordToComplete: wordToComplete);
                    goto default;
                case "checkout":
                    if (string.IsNullOrEmpty(value: completeCommandParameters.PreviousParameterName) && !isCompletingParameterName) return CompleteCheckout(completeCommandParameters);
                    goto default;
                case "fetch":
                    if (!isCompletingParameterName)
                    {
                        if (!string.IsNullOrEmpty(value: completeCommandParameters.PreviousParameterValue) && completeCommandParameters.PreviousParameterValue != completeCommandParameters.CommandName)
                            return Git.RemoteRefs()
                                .Where(r => r.Remote.IgnoreCaseEquals(value: completeCommandParameters.PreviousParameterValue) && r.Ref.IgnoreCaseStartsWith(value: wordToComplete))
                                .Select(c => new CompletionResult(completionText: c.Ref, listItemText: c.Ref, resultType: CompletionResultType.ParameterValue,
                                    toolTip: c.RemoteRef))
                                .ToList();
                        return Git.RemoteRefs()
                            .Where(r => r.Remote.IgnoreCaseStartsWith(value: wordToComplete))
                            .GroupBy(c => c.Remote)
                            .Select(g => g.First())
                            .Select(c => new CompletionResult(completionText: c.Remote, listItemText: c.Remote, resultType: CompletionResultType.ParameterValue, toolTip: c.Remote))
                            .ToList();
                    }

                    goto default;
                case "diff":
                case "difftool":
                    return CompleteDiff(completeCommandParameters);
                case "rebase":
                    if (wordToComplete.IsEmpty())
                        return Git.Log()
                            .Select(log => new CompletionResult(completionText: log.Commit, listItemText: log.Commit, resultType: CompletionResultType.ParameterValue, toolTip: log.Message))
                            .ToList();
                    else if (!isCompletingParameterName)
                        return Git.Log()
                            .Where(l => l.Commit.IgnoreCaseStartsWith(value: wordToComplete) || l.Message.IgnoreCaseStartsWith(value: wordToComplete))
                            .Select(log => new CompletionResult(completionText: log.Commit, listItemText: log.Commit, resultType: CompletionResultType.ParameterValue, toolTip: log.Message))
                            .ToList();
                    goto default;
                default:
                    return isCompletingParameterName
                        ? (IList<CompletionResult>) GitOptionsToCompletionResults(gitCommandOptions: completeCommandParameters.GitCommandOptions,
                            wordToComplete: wordToComplete)
                        : Array.Empty<CompletionResult>();
            }
        }

        private static IList<CompletionResult> CompleteDiff(CompleteCommandParameters completeCommandParameters) {
            var wordToComplete = completeCommandParameters.WordToComplete;
            var commits = completeCommandParameters.Ast.CommandElements.Skip(1)
                .Where(ce => !ce.Extent.Text.StartsWith("-"))
                .Take(3)
                .Select(ce => ce.Extent.Text)
                .Skip(1).ToArray();
            var cached = completeCommandParameters.Ast.Extent.Text.Contains("--cached");
            var (from, to) = commits switch {
                {Length: 1} => (commits[0], null),
                {Length: 2} => (commits[0], commits[1]),
                _ => (null, null),
            };

            if (completeCommandParameters.AfterDoubleDash) {

                return Git.GetDiffableFiles(match:wordToComplete, from, to, cached).Select(f=>new CompletionResult(f)).ToList();
            }

            if (wordToComplete.IsEmpty())
                return Git.Log()
                    .Select(log => new CompletionResult(completionText: log.Commit, listItemText: log.Commit, resultType: CompletionResultType.ParameterValue, toolTip: log.Message))
                    .ToList();
            return completeCommandParameters.IsCompletingParameterName
                ? GitOptionsToCompletionResults(completeCommandParameters.GitCommandOptions, wordToComplete)
                : Git.Log()
                    .Where(l => l.Commit.IgnoreCaseStartsWith(value: wordToComplete) || l.Message.IgnoreCaseStartsWith(value: wordToComplete))
                    .Select(log => new CompletionResult(completionText: log.Commit, listItemText: log.Commit,
                        resultType: CompletionResultType.ParameterValue, toolTip: log.Message))
                    .ToList();
        }


        private static IList<CompletionResult> CompleteCheckout(CompleteCommandParameters completeCommandParameters) {
            var wordToComplete = completeCommandParameters.WordToComplete;
            return completeCommandParameters.AfterDoubleDash ? CompleteModifiedFiles(wordToComplete) : CompleteBranches(wordToComplete);
        }

        private static IList<CompletionResult> CompleteModifiedFiles(string wordToComplete) {
            return Git.Status()
                .Where(s=>s.WorkTreeStatus != GitStatusKind.None && s.Path.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                .Select(status => new CompletionResult(status.Path, status.Path, CompletionResultType.ProviderItem, $"status: {status.WorkTreeStatus}"))
                .ToArray();
        }

        private static IList<CompletionResult> CompleteBranches(string wordToComplete)
        {
            return Git.Heads(match: wordToComplete)
                .Select(c => new CompletionResult(completionText: c, listItemText: c, resultType: CompletionResultType.ParameterValue, toolTip: c))
                .ToList();
        }

        private static List<CompletionResult> GitOptionsToCompletionResults(GitCommandOption[] gitCommandOptions, string wordToComplete)
        {
            return gitCommandOptions.Where(option => option.Completion.StartsWith(value: wordToComplete))
                .Select(o => o.ToCompletionResult())
                .ToList();
        }

        private static IList<CompletionResult> CompleteGitCommands(string wordToComplete)
        {
            var commands = GitCommand.Commands.Where(c => c.Name.IgnoreCaseStartsWith(value: wordToComplete))
                .Select(c => c.ToCompletionResult())
                .ToList();
            var aliases = Git.GetAliases(wordToComplete).Select(a => new CompletionResult(a.alias, a.alias, CompletionResultType.Command, $"alias: {a.command} {a.parameters}"));
            commands.AddRange(aliases);
            commands.Sort((result, completionResult) => string.CompareOrdinal(result.CompletionText, completionResult.CompletionText));
            return commands;
        }
    }

    public struct GitRef
    {
        public string Commit { get; }
        public string Name { get; }

        public GitRef(string commit, string name)
        {
            Commit = commit;
            Name = name;
        }
    }
}