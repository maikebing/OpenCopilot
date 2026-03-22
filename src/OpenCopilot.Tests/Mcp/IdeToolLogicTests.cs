using System;
using System.IO;
using System.Linq;
using OpenCopilot.Mcp;
using Xunit;

namespace OpenCopilot.Tests.Mcp
{
    public class IdeToolLogicTests
    {
        [Fact]
        public void ExtractAnchorWindow_ReturnsRegexWindow_WhenRegexEnabled()
        {
            var text = "alpha beta123 gamma";
            var request = new IdeReadAnchorRequest("beta\\d+", beforeChars: 2, afterChars: 3, useRegex: true);

            var window = IdeToolLogic.ExtractAnchorWindow(text, request);

            Assert.Equal("a beta123 ga", window);
        }

        [Fact]
        public void ExtractAnchorWindow_MatchesIgnoringCase_WhenIgnoreCaseEnabled()
        {
            var text = "prefix TargetText suffix";
            var request = new IdeReadAnchorRequest("targettext", beforeChars: 0, afterChars: 0, ignoreCase: true);

            var window = IdeToolLogic.ExtractAnchorWindow(text, request);

            Assert.Equal("TargetText", window);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsMethodName_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrder() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("SaveOrder", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsQualifiedMethodName_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrder() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("Demo.App.OrderService.SaveOrder", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsCommonWhenVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("WhenSaveOrder", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsCommonShouldVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("SaveOrder_Should", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsPrefixedShouldVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("Should_SaveOrder", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsGivenWhenVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("Given_SaveOrder_When", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsGivenWhenThenVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("Given_SaveOrder_When_SaveOrder_Then", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsTypePlusMethodGivenWhenThenVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("OrderService_Given_SaveOrder_When_SaveOrder_Then", tokens);
        }

        [Fact]
        public void InferTestFilterTokens_ContainsNamespaceTypePlusMethodGivenWhenThenVariant_WhenMethodDeclared()
        {
            var source = "namespace Demo.App; public class OrderService { public void SaveOrderAsync() { } }";

            var tokens = IdeToolLogic.InferTestFilterTokens(source, "OrderService.cs");

            Assert.Contains("Demo.App.OrderService_Given_SaveOrder_When_SaveOrder_Then", tokens);
        }

        [Fact]
        public void ParsePatchOperations_ReturnsRenameOperation_WhenDiffContainsRenameHeaders()
        {
            var patch = "diff --git a/src/OldName.cs b/src/NewName.cs\nrename from src/OldName.cs\nrename to src/NewName.cs\n--- a/src/OldName.cs\n+++ b/src/NewName.cs\n";

            var operations = IdeToolLogic.ParsePatchOperations(null, patch);

            Assert.Equal(IdePatchOperationKind.Rename, operations.Single().Kind);
        }

        [Fact]
        public void ParsePatchOperations_PreservesRenameSourcePath_WhenDiffContainsRenameHeaders()
        {
            var patch = "diff --git a/src/OldName.cs b/src/NewName.cs\nrename from src/OldName.cs\nrename to src/NewName.cs\n--- a/src/OldName.cs\n+++ b/src/NewName.cs\n";

            var operations = IdeToolLogic.ParsePatchOperations(null, patch);

            Assert.Equal("src/OldName.cs", operations.Single().SourcePath);
        }

        [Fact]
        public void ParsePatchOperations_ReturnsCopyOperation_WhenDiffContainsCopyHeaders()
        {
            var patch = "diff --git a/src/Source.cs b/src/Copy.cs\ncopy from src/Source.cs\ncopy to src/Copy.cs\n--- a/src/Source.cs\n+++ b/src/Copy.cs\n";

            var operations = IdeToolLogic.ParsePatchOperations(null, patch);

            Assert.Equal(IdePatchOperationKind.Copy, operations.Single().Kind);
        }

        [Fact]
        public void ParsePatchOperations_PreservesUpdatedMode_WhenDiffContainsModeChange()
        {
            var patch = "diff --git a/src/File.cs b/src/File.cs\nold mode 100644\nnew mode 100755\n--- a/src/File.cs\n+++ b/src/File.cs\n";

            var operations = IdeToolLogic.ParsePatchOperations(null, patch);

            Assert.Equal("100755", operations.Single().UpdatedMode);
        }

        [Fact]
        public void ApplyPatchWrites_CreatesFile_WhenCreateWriteApplied()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            var operations = new[]
            {
                new IdePatchFileOperation("created.txt", new[] { new IdePatchBlock("seed", "hello") }, IdePatchOperationKind.Create)
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);
            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "created.txt")));
        }

        [Fact]
        public void ApplyPatchWrites_DeletesFile_WhenDeleteWriteApplied()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "deleted.txt");
            File.WriteAllText(filePath, "remove");
            var operations = new[]
            {
                new IdePatchFileOperation("deleted.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Delete)
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);
            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public void ApplyPatchWrites_RenamesFile_WhenRenameWriteApplied()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var originalPath = Path.Combine(root, "OldName.txt");
            File.WriteAllText(originalPath, "content");
            var operations = new[]
            {
                new IdePatchFileOperation("NewName.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Rename, "OldName.txt")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);
            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.False(File.Exists(originalPath));
        }

        [Fact]
        public void ApplyPatchWrites_CopiesFile_WhenCopyWriteApplied()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "Source.txt");
            var copyPath = Path.Combine(root, "Copy.txt");
            File.WriteAllText(sourcePath, "content");
            var operations = new[]
            {
                new IdePatchFileOperation("Copy.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Copy, "Source.txt")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);
            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.True(File.Exists(copyPath));
        }

        [Fact]
        public void ApplyPatchWrites_SetsReadOnlyAttribute_WhenWindowsModeRemovesWriteBits()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            var filePath = Path.Combine(root, "readonly.txt");
            var writes = new[]
            {
                new IdePatchWrite(filePath, "content", IdePatchOperationKind.Create, updatedMode: "100444")
            };

            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.True(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));
        }

        [Fact]
        public void ApplyPatchWrites_ClearsReadOnlyAttribute_WhenWindowsModeAddsWriteBits()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "writable.txt");
            File.WriteAllText(filePath, "content");
            File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);
            var writes = new[]
            {
                new IdePatchWrite(filePath, "content", IdePatchOperationKind.Modify, updatedMode: "100644")
            };

            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.False(File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly));
        }

        [Fact]
        public void ApplyPatchWrites_SetsArchiveAttribute_WhenWindowsModeMapsRegularFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "archive.txt");
            File.WriteAllText(filePath, "content");
            File.SetAttributes(filePath, File.GetAttributes(filePath) & ~FileAttributes.Archive);
            var writes = new[]
            {
                new IdePatchWrite(filePath, "content", IdePatchOperationKind.Modify, updatedMode: "100644")
            };

            IdeToolLogic.ApplyPatchWrites(writes);

            Assert.True(File.GetAttributes(filePath).HasFlag(FileAttributes.Archive));
        }

        [Fact]
        public void PreparePatchWrites_PreservesUpdatedMode_WhenOperationContainsModeMetadata()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "mode.txt");
            File.WriteAllText(filePath, "content");
            var operations = new[]
            {
                new IdePatchFileOperation("mode.txt", new[] { new IdePatchBlock("content", "updated") }, IdePatchOperationKind.Modify, null, "100644", "100755")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);

            Assert.Equal("100755", writes.Single().UpdatedMode);
        }

        [Fact]
        public void ResolveProjectAttachmentContainerPath_ReturnsRelatedPath_WhenTargetSharesRelatedDirectory()
        {
            var relatedPath = Path.Combine("C:\\repo", "src", "File.cs");

            var containerPath = IdeToolLogic.ResolveProjectAttachmentContainerPath(
                Path.Combine("C:\\repo", "src", "Copy.cs"),
                relatedPath,
                Array.Empty<string>(),
                Array.Empty<string>());

            Assert.Equal(relatedPath, containerPath);
        }

        [Fact]
        public void ResolveRelatedAttachmentPath_ReturnsRelatedPath_WhenTargetSharesDirectory()
        {
            var relatedPath = Path.Combine(@"C:\repo", "src", "File.cs");

            var containerPath = IdeToolLogic.ResolveRelatedAttachmentPath(Path.Combine(@"C:\repo", "src"), relatedPath);

            Assert.Equal(relatedPath, containerPath);
        }

        [Fact]
        public void ResolveKnownAttachmentContainerPath_ReturnsKnownFolder_WhenFolderMatches()
        {
            var folderPath = Path.Combine(@"C:\repo", "src", "Nested");

            var containerPath = IdeToolLogic.ResolveKnownAttachmentContainerPath(Path.Combine(@"C:\repo", "src", "Nested"), new[] { folderPath });

            Assert.Equal(folderPath, containerPath);
        }

        [Fact]
        public void ResolveContainingProjectRoot_ReturnsDeepestProjectRoot_WhenMultipleRootsMatch()
        {
            var containerPath = IdeToolLogic.ResolveContainingProjectRoot(
                Path.Combine(@"C:\repo", "src", "ProjectA", "Folder", "File.cs"),
                new[] { Path.Combine(@"C:\repo", "src"), Path.Combine(@"C:\repo", "src", "ProjectA") });

            Assert.Equal(Path.Combine(@"C:\repo", "src", "ProjectA"), containerPath);
        }

        [Fact]
        public void ResolveProjectAttachmentContainerPath_ReturnsKnownFolderItem_WhenTargetDirectoryIsKnown()
        {
            var folderPath = Path.Combine("C:\\repo", "src", "Nested");

            var containerPath = IdeToolLogic.ResolveProjectAttachmentContainerPath(
                Path.Combine(folderPath, "File.cs"),
                null,
                new[] { folderPath },
                Array.Empty<string>());

            Assert.Equal(folderPath, containerPath);
        }

        [Fact]
        public void ResolveProjectAttachmentContainerPath_ReturnsProjectRoot_WhenSpecialProjectHasNoKnownFolderItem()
        {
            var projectRoot = Path.Combine("C:\\repo", "src", "ProjectA");

            var containerPath = IdeToolLogic.ResolveProjectAttachmentContainerPath(
                Path.Combine(projectRoot, "Folder", "File.cs"),
                null,
                Array.Empty<string>(),
                new[] { string.Empty, projectRoot });

            Assert.Equal(projectRoot, containerPath);
        }

        [Fact]
        public void ShouldTryOpenDocumentSync_ReturnsTrue_WhenCopyTargetIsActive()
        {
            var write = new IdePatchWrite("C:\\repo\\copy.cs", "content", IdePatchOperationKind.Copy);

            var shouldSync = IdeToolLogic.ShouldTryOpenDocumentSync("C:\\repo\\copy.cs", write);

            Assert.True(shouldSync);
        }

        [Fact]
        public void ShouldTryOpenDocumentSync_ReturnsFalse_WhenRenameSourceIsActive()
        {
            var write = new IdePatchWrite("C:\\repo\\target.cs", "content", IdePatchOperationKind.Rename, "C:\\repo\\source.cs");

            var shouldSync = IdeToolLogic.ShouldTryOpenDocumentSync("C:\\repo\\source.cs", write);

            Assert.False(shouldSync);
        }

        [Fact]
        public void DetermineOpenDocumentSyncAction_ReturnsCopySourceToTarget_WhenCopySourceIsActive()
        {
            var write = new IdePatchWrite("C:\\repo\\target.cs", "content", IdePatchOperationKind.Copy, "C:\\repo\\source.cs");

            var action = IdeToolLogic.DetermineOpenDocumentSyncAction("C:\\repo\\source.cs", write);

            Assert.Equal(IdeOpenDocumentSyncAction.CopySourceToTarget, action);
        }

        [Fact]
        public void DetermineOpenDocumentSyncAction_ReturnsRenameSourceToTarget_WhenRenameSourceIsActive()
        {
            var write = new IdePatchWrite("C:\\repo\\target.cs", "content", IdePatchOperationKind.Rename, "C:\\repo\\source.cs");

            var action = IdeToolLogic.DetermineOpenDocumentSyncAction("C:\\repo\\source.cs", write);

            Assert.Equal(IdeOpenDocumentSyncAction.RenameSourceToTarget, action);
        }

        [Fact]
        public void PreparePatchWrites_Throws_WhenDuplicateTargetPathsConflict()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            var operations = new[]
            {
                new IdePatchFileOperation("conflict.txt", new[] { new IdePatchBlock("seed", "one") }, IdePatchOperationKind.Create),
                new IdePatchFileOperation("conflict.txt", new[] { new IdePatchBlock("seed", "two") }, IdePatchOperationKind.Create)
            };

            var exception = Assert.Throws<InvalidOperationException>(() => IdeToolLogic.PreparePatchWrites(root, operations));

            Assert.Equal("Patch contains duplicate target paths.", exception.Message);
        }

        [Fact]
        public void PreparePatchWrites_Throws_WhenCreateTargetAlreadyExists()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "existing.txt"), "already there");
            var operations = new[]
            {
                new IdePatchFileOperation("existing.txt", new[] { new IdePatchBlock("seed", "new") }, IdePatchOperationKind.Create)
            };

            var exception = Assert.Throws<InvalidOperationException>(() => IdeToolLogic.PreparePatchWrites(root, operations));

            Assert.Equal("Patch target already exists.", exception.Message);
        }

        [Fact]
        public void PreparePatchWrites_SkipsCreateWrite_WhenExistingTargetHasSameContent()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "existing.txt"), "same");
            var operations = new[]
            {
                new IdePatchFileOperation("existing.txt", new[] { new IdePatchBlock("seed", "same") }, IdePatchOperationKind.Create)
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);

            Assert.Empty(writes);
        }

        [Fact]
        public void PreparePatchWrites_ReusesExistingRenameTarget_WhenContentMatches()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "source.txt"), "same");
            File.WriteAllText(Path.Combine(root, "target.txt"), "same");
            var operations = new[]
            {
                new IdePatchFileOperation("target.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Rename, "source.txt")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);

            Assert.False(writes.Single().ShouldWriteContent);
        }

        [Fact]
        public void PreparePatchWrites_Throws_WhenRenameTargetHasDifferentContent()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "source.txt"), "source");
            File.WriteAllText(Path.Combine(root, "target.txt"), "different");
            var operations = new[]
            {
                new IdePatchFileOperation("target.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Rename, "source.txt")
            };

            var exception = Assert.Throws<InvalidOperationException>(() => IdeToolLogic.PreparePatchWrites(root, operations));

            Assert.Equal("Rename target already exists with different content.", exception.Message);
        }

        [Fact]
        public void PreparePatchWrites_PrefersMoveForSimpleRename_WhenTargetDoesNotExist()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "source.txt"), "content");
            var operations = new[]
            {
                new IdePatchFileOperation("target.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Rename, "source.txt")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);

            Assert.True(writes.Single().PreferMove);
        }

        [Fact]
        public void PreparePatchWrites_SkipsCopyWrite_WhenExistingTargetHasSameContent()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "source.txt"), "same");
            File.WriteAllText(Path.Combine(root, "copy.txt"), "same");
            var operations = new[]
            {
                new IdePatchFileOperation("copy.txt", Array.Empty<IdePatchBlock>(), IdePatchOperationKind.Copy, "source.txt")
            };

            var writes = IdeToolLogic.PreparePatchWrites(root, operations);

            Assert.Empty(writes);
        }

        [Fact]
        public void PreparePatchWrites_Throws_WhenTargetPathsConflictAtDirectoryLevel()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            var operations = new[]
            {
                new IdePatchFileOperation("folder", new[] { new IdePatchBlock("seed", "one") }, IdePatchOperationKind.Create),
                new IdePatchFileOperation("folder\\child.txt", new[] { new IdePatchBlock("seed", "two") }, IdePatchOperationKind.Create)
            };

            var exception = Assert.Throws<InvalidOperationException>(() => IdeToolLogic.PreparePatchWrites(root, operations));

            Assert.Equal("Patch contains directory-level target conflicts.", exception.Message);
        }

        [Fact]
        public void PreparePatchWrites_Throws_WhenDirectoryChainContainsExistingFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "OpenCopilot-IdeToolLogicTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "folder"), "blocker");
            var operations = new[]
            {
                new IdePatchFileOperation("folder\\child.txt", new[] { new IdePatchBlock("seed", "value") }, IdePatchOperationKind.Create)
            };

            var exception = Assert.Throws<InvalidOperationException>(() => IdeToolLogic.PreparePatchWrites(root, operations));

            Assert.Equal("Patch target conflicts with an existing file in its directory chain.", exception.Message);
        }
    }
}
