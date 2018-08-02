﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.StrongNamed;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Razor.LanguageServer.ProjectSystem
{
    internal class DefaultRazorProjectService : RazorProjectService
    {
        private readonly ProjectSnapshotManagerShimAccessor _projectSnapshotManagerAccessor;
        private readonly RazorConfigurationResolver _configurationResolver;
        private readonly ForegroundDispatcherShim _foregroundDispatcher;
        private readonly ProjectResolver _projectResolver;
        private readonly FilePathNormalizer _filePathNormalizer;
        private readonly VSCodeLogger _logger;

        public DefaultRazorProjectService(
            ForegroundDispatcherShim foregroundDispatcher,
            ProjectResolver projectResolver,
            FilePathNormalizer filePathNormalizer,
            ProjectSnapshotManagerShimAccessor projectSnapshotManagerAccessor,
            RazorConfigurationResolver configurationResolver,
            VSCodeLogger logger)
        {
            if (foregroundDispatcher == null)
            {
                throw new ArgumentNullException(nameof(foregroundDispatcher));
            }

            if (projectResolver == null)
            {
                throw new ArgumentNullException(nameof(projectResolver));
            }

            if (filePathNormalizer == null)
            {
                throw new ArgumentNullException(nameof(filePathNormalizer));
            }

            if (projectSnapshotManagerAccessor == null)
            {
                throw new ArgumentNullException(nameof(projectSnapshotManagerAccessor));
            }

            if (configurationResolver == null)
            {
                throw new ArgumentNullException(nameof(configurationResolver));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _projectSnapshotManagerAccessor = projectSnapshotManagerAccessor;
            _configurationResolver = configurationResolver;
            _foregroundDispatcher = foregroundDispatcher;
            _projectResolver = projectResolver;
            _filePathNormalizer = filePathNormalizer;
            _logger = logger;
        }

        public override void AddDocument(string text, Uri uri)
        {
            _foregroundDispatcher.AssertForegroundThread();

            var textDocumentPath = _filePathNormalizer.Normalize(uri.AbsolutePath);
            if (!_projectResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
            {
                projectSnapshot = _projectResolver.GetMiscellaneousProject();
            }

            var hostDocument = HostDocumentShim.Create(textDocumentPath, textDocumentPath);
            var sourceText = SourceText.From(text);
            var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp.Default);
            var textLoader = TextLoader.From(textAndVersion);
            _projectSnapshotManagerAccessor.Instance.DocumentAdded(projectSnapshot.HostProject, hostDocument, textLoader);

            _logger.Log($"Added document '{textDocumentPath}' to project {projectSnapshot.FilePath} in the Razor project system.");
        }

        public override void RemoveDocument(Uri textDocumentUri)
        {
            _foregroundDispatcher.AssertForegroundThread();

            var textDocumentPath = _filePathNormalizer.Normalize(textDocumentUri.AbsolutePath);
            if (!_projectResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
            {
                projectSnapshot = _projectResolver.GetMiscellaneousProject();
            }

            if (!projectSnapshot.DocumentFilePaths.Contains(textDocumentPath, FilePathComparerShim.Instance))
            {
                _logger.Log($"Containing project is not tracking document '{textDocumentUri.LocalPath}");
                return;
            }

            var document = projectSnapshot.GetDocument(textDocumentPath);
            _projectSnapshotManagerAccessor.Instance.DocumentRemoved(projectSnapshot.HostProject, document.HostDocument);

            _logger.Log($"Removed document '{textDocumentPath}' from project {projectSnapshot.FilePath} in the Razor project system.");
        }

        public override void AddProject(string filePath, RazorConfiguration configuration)
        {
            _foregroundDispatcher.AssertForegroundThread();

            var normalizedPath = _filePathNormalizer.Normalize(filePath);
            var hostProject = HostProjectShim.Create(normalizedPath, configuration);
            _projectSnapshotManagerAccessor.Instance.HostProjectAdded(hostProject);
            _logger.Log($"Added project '{filePath}' to the Razor project system.");

            TryMigrateMiscellaneousDocuments();
        }

        private void TryMigrateMiscellaneousDocuments()
        {
            var miscellaneousProject = _projectResolver.GetMiscellaneousProject();

            foreach (var documentFilePath in miscellaneousProject.DocumentFilePaths)
            {
                if (_projectResolver.TryResolveProject(documentFilePath, out var projectSnapshot))
                {
                    var documentSnapshot = miscellaneousProject.GetDocument(documentFilePath);
                    
                    // Remove from miscellaneous project
                    _projectSnapshotManagerAccessor.Instance.DocumentRemoved(miscellaneousProject.HostProject, documentSnapshot.HostDocument);

                    // Add to new project

                    // HACK, this should probably all be async.
                    var sourceText = documentSnapshot.GetTextAsync().Result;
                    var textAndVersion = TextAndVersion.Create(sourceText, VersionStamp.Default);
                    var textLoader = TextLoader.From(textAndVersion);

                    _projectSnapshotManagerAccessor.Instance.DocumentAdded(projectSnapshot.HostProject, documentSnapshot.HostDocument, textLoader);

                    _logger.Log($"Migrated '{documentFilePath}' from the Miscellaneous Project to project '{projectSnapshot.FilePath}'.");
                }
            }
        }
    }
}
