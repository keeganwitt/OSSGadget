﻿// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

using DiscUtils.Streams;
using Microsoft.CST.OpenSource.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Version = SemVer.Version;

namespace Microsoft.CST.OpenSource.Shared
{
    internal class NPMProjectManager : BaseProjectManager
    {
        public static string ENV_NPM_API_ENDPOINT = "https://registry.npmjs.org";
        public static string ENV_NPM_ENDPOINT = "https://www.npmjs.com";

        public NPMProjectManager(string destinationDirectory) : base(destinationDirectory)
        {
        }

        /// <summary>
        ///     Download one NPM package and extract it to the target directory.
        /// </summary>
        /// <param name="purl"> Package URL of the package to download. </param>
        /// <returns> n/a </returns>
        public override async Task<IEnumerable<string>> DownloadVersion(PackageURL purl, bool doExtract, bool cached = false)
        {
            Logger.Trace("DownloadVersion {0}", purl?.ToString());

            var packageName = purl?.Name;
            var packageVersion = purl?.Version;
            var downloadedPaths = new List<string>();

            // shouldn't happen here, but check
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(packageVersion))
            {
                Logger.Error("Unable to download [{0} {1}]. Both must be defined.", packageName, packageVersion);
                return downloadedPaths;
            }

            try
            {
                var doc = await GetJsonCache($"{ENV_NPM_API_ENDPOINT}/{packageName}");
                var tarball = doc.RootElement.GetProperty("versions").GetProperty(packageVersion).GetProperty("dist").GetProperty("tarball").GetString();
                var result = await WebClient.GetAsync(tarball);
                result.EnsureSuccessStatusCode();
                Logger.Debug("Downloading {0}...", purl?.ToString());
                var targetName = $"npm-{packageName}@{packageVersion}";
                string extractionPath = Path.Combine(TopLevelExtractionDirectory, targetName);
                if (doExtract && Directory.Exists(extractionPath) && cached == true)
                {
                    downloadedPaths.Add(extractionPath);
                    return downloadedPaths;
                }
                if (doExtract)
                {
                    downloadedPaths.Add(await ExtractArchive(targetName, await result.Content.ReadAsByteArrayAsync(), cached));
                }
                else
                {
                    targetName += Path.GetExtension(tarball) ?? "";
                    await File.WriteAllBytesAsync(targetName, await result.Content.ReadAsByteArrayAsync());
                    downloadedPaths.Add(targetName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error downloading NPM package: {0}", ex.Message);
            }
            return downloadedPaths;
        }

        public override async Task<IEnumerable<string>> EnumerateVersions(PackageURL purl)
        {
            Logger.Trace("EnumerateVersions {0}", purl?.ToString());
            if (purl == null)
            {
                return new List<string>();
            }

            try
            {
                var packageName = purl.Name;
                var doc = await GetJsonCache($"{ENV_NPM_API_ENDPOINT}/{packageName}");
                var versionList = new List<string>();

                foreach (var versionKey in doc.RootElement.GetProperty("versions").EnumerateObject())
                {
                    Logger.Debug("Identified {0} version {1}.", packageName, versionKey.Name);
                    versionList.Add(versionKey.Name);
                }
                var latestVersion = doc.RootElement.GetProperty("dist-tags").GetProperty("latest").GetString();
                Logger.Debug("Identified {0} version {1}.", packageName, latestVersion);
                versionList.Add(latestVersion);

                return SortVersions(versionList.Distinct());
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error enumerating NPM package: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        ///     Gets the latest version of the package
        /// </summary>
        /// <param name="contentJSON"> </param>
        /// <returns> </returns>
        public JsonElement? GetLatestVersionElement(JsonDocument contentJSON)
        {
            List<Version> versions = GetVersions(contentJSON);
            Version? maxVersion = GetLatestVersion(versions);
            if (maxVersion is null) { return null; }
            return GetVersionElement(contentJSON, maxVersion);
        }

        public override async Task<string?> GetMetadata(PackageURL purl)
        {
            try
            {
                var packageName = purl.Name;
                var content = await GetHttpStringCache($"{ENV_NPM_API_ENDPOINT}/{packageName}");
                return content;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error fetching NPM metadata: {ex.Message}");
                return null;
            }
        }

        public override Uri GetPackageAbsoluteUri(PackageURL purl)
        {
            return new Uri($"{ENV_NPM_API_ENDPOINT}/package/{purl?.Name}");
        }

        /// <summary>
        ///     Gets the structured metadata for the npm package
        /// </summary>
        /// <param name="purl"> </param>
        /// <returns> </returns>
        public override async Task<PackageMetadata> GetPackageMetadata(PackageURL purl)
        {
            PackageMetadata metadata = new PackageMetadata();
            string? content = await GetMetadata(purl);
            if (string.IsNullOrEmpty(content)) { return metadata; }

            // convert NPM package data to normalized form
            JsonDocument contentJSON = JsonDocument.Parse(content);
            JsonElement root = contentJSON.RootElement;

            metadata.Name = root.GetProperty("name").GetString();
            metadata.Description = root.GetProperty("description").GetString();

            metadata.PackageManagerUri = ENV_NPM_ENDPOINT;
            metadata.Platform = "NPM";
            metadata.Language = "JavaScript";
            metadata.Package_Uri = $"{metadata.PackageManagerUri}/package/{metadata.Name}";

            var versions = GetVersions(contentJSON);
            var latestVersion = GetLatestVersion(versions);

            if (purl.Version != null)
            {
                // find the version object from the collection
                metadata.PackageVersion = purl.Version;
            }
            else
            {
                metadata.PackageVersion = latestVersion is null ? purl.Version : latestVersion?.ToString();
            }

            // if we found any version at all, get the deets
            if (metadata.PackageVersion is not null)
            {
                Version versionToGet = new Version(metadata.PackageVersion);
                JsonElement? versionElement = GetVersionElement(contentJSON, versionToGet);
                if (versionElement is not null)
                {
                    // redo the generic values to version specific values
                    metadata.Package_Uri = $"{ENV_NPM_ENDPOINT}/package/{metadata.Name}";
                    metadata.VersionUri = $"{ENV_NPM_ENDPOINT}/package/{metadata.Name}/v/{metadata.PackageVersion}";
                    metadata.VersionDownloadUri = $"{ENV_NPM_API_ENDPOINT}/{metadata.Name}/-/{metadata.Name}-{metadata.PackageVersion}.tgz";

                    // author(s)
                    var authorElement = Utilities.GetJSONPropertyIfExists(versionElement, "author");
                    User author = new User();
                    if (authorElement is not null)
                    {
                        author.Name = Utilities.GetJSONPropertyIfExists(authorElement, "name")?.ToString();
                        author.Email = Utilities.GetJSONPropertyIfExists(authorElement, "email")?.ToString();
                        author.Url = Utilities.GetJSONPropertyIfExists(authorElement, "url")?.ToString();

                        metadata.Authors ??= new List<User>();
                        metadata.Authors.Add(author);
                    }

                    // maintainers
                    var maintainersElement = Utilities.GetJSONPropertyIfExists(versionElement, "maintainers");
                    if (maintainersElement?.EnumerateArray() is JsonElement.ArrayEnumerator enumerator)
                    {
                        foreach (JsonElement maintainerElement in enumerator)
                        {
                            User maintainer = new User
                            {
                                Name = Utilities.GetJSONPropertyIfExists(maintainerElement, "name").ToString(),
                                Email = Utilities.GetJSONPropertyIfExists(maintainerElement, "email").ToString(),
                                Url = Utilities.GetJSONPropertyIfExists(maintainerElement, "url").ToString()
                            };
                            metadata.Maintainers = new List<User>();
                            metadata.Maintainers.Add(maintainer);
                        }
                    }

                    // repository
                    var repoMappings = await SearchRepoUrlsInPackageMetadata(purl, content);
                    foreach (var repoMapping in repoMappings)
                    {
                        Repository repository = new Repository
                        {
                            Rank = repoMapping.Value,
                            Type = repoMapping.Key.Type
                        };
                        await repository.GetGithubRepositoryMetadata(repoMapping.Key);

                        metadata.Repository ??= new List<Repository>();
                        metadata.Repository.Add(repository);
                    }

                    // keywords
                    var keywords = Utilities.GetJSONPropertyIfExists(versionElement, "keywords");
                    if (keywords is not null)
                    {
                        foreach (var keyword in keywords.Value.EnumerateArray())
                        {
                            metadata.Keywords ??= new List<string>();
                            if (keyword.ToString() is string s)
                            {
                                metadata.Keywords.Add(s);
                            }
                        }

                        // licenses
                        var licenses = Utilities.GetJSONPropertyIfExists(versionElement, "licenses");
                        if (licenses is not null)
                        {
                            metadata.Licenses ??= new List<License>();
                            // TODO: Convert/append SPIX_ID values?
                            foreach (var license in licenses.Value.EnumerateArray())
                            {
                                metadata.Licenses.Add(new License()
                                {
                                    Name = Utilities.GetJSONPropertyIfExists(license, "type").ToString(),
                                    Url = Utilities.GetJSONPropertyIfExists(license, "url").ToString()
                                });
                            }
                        }
                    }
                }
            }
            if (latestVersion is not null)
            {
                metadata.LatestPackageVersion = latestVersion.ToString();
            }

            return metadata;
        }

        public override JsonElement? GetVersionElement(JsonDocument contentJSON, Version version)
        {
            JsonElement root = contentJSON.RootElement;

            try
            {
                JsonElement versionsJSON = root.GetProperty("versions");
                foreach (JsonProperty versionProperty in versionsJSON.EnumerateObject())
                {
                    if (versionProperty.Name == version.ToString())
                    {
                        return versionsJSON.GetProperty(version.ToString());
                    }
                }
            }
            catch (KeyNotFoundException) { return null; }
            catch (InvalidOperationException) { return null; }

            return null;
        }

        public override List<Version> GetVersions(JsonDocument contentJSON)
        {
            List<Version> allVersions = new List<Version>();
            JsonElement root = contentJSON.RootElement;
            try
            {
                JsonElement versions = root.GetProperty("versions");
                foreach (var version in versions.EnumerateObject())
                {
                    allVersions.Add(new Version(version.Name));
                }
            }
            catch (KeyNotFoundException) { return allVersions; }
            catch (InvalidOperationException) { return allVersions; }

            return allVersions;
        }

        /// <summary>
        ///     Searches the package manager metadata to figure out the source code repository
        /// </summary>
        /// <param name="purl"> the package for which we need to find the source code repository </param>
        /// <returns>
        ///     A dictionary, mapping each possible repo source entry to its probability/empty dictionary
        /// </returns>

        protected async override Task<Dictionary<PackageURL, double>> SearchRepoUrlsInPackageMetadata(PackageURL purl,
            string metadata)
        {
            if (string.IsNullOrEmpty(metadata))
            {
                return new Dictionary<PackageURL, double>();
            }
            JsonDocument contentJSON = JsonDocument.Parse(metadata);
            return await SearchRepoUrlsInPackageMetadata(purl, contentJSON);
        }

        protected async Task<Dictionary<PackageURL, double>> SearchRepoUrlsInPackageMetadata(PackageURL purl,
            JsonDocument contentJSON)
        {
            var mapping = new Dictionary<PackageURL, double>();
            if (purl?.Name is string purlName && (purlName.StartsWith('_') || npm_internal_modules.Contains(purlName)))
            {
                // url = 'https://github.com/nodejs/node/tree/master/lib' + package.name,

                mapping.Add(new PackageURL(purl.Type, purl.Namespace, purl.Name,
                    null, null, "node/tree/master/lib"), 1.0F);
                return mapping;
            }

            // if a version is provided, search that JSONElement, otherwise, just search the latest version,
            // which is more likely best maintained
            // TODO: If the latest version JSONElement doesnt have the repo infor, should we search all elements
            // on that chance that one of them might have it?
            JsonElement? versionJSON = string.IsNullOrEmpty(purl?.Version) ? GetLatestVersionElement(contentJSON) :
                GetVersionElement(contentJSON, new Version(purl.Version));

            if (versionJSON is JsonElement notNullVersionJSON)
            {
                try
                {
                    JsonElement repositoryJSON = notNullVersionJSON.GetProperty("repository");
                    string repoType = repositoryJSON.GetProperty("type").ToString().ToLower();
                    string repoURL = repositoryJSON.GetProperty("url").ToString();

                    // right now we deal with only github repos
                    if (repoType == "git")
                    {
                        PackageURL gitPURL = GitHubProjectManager.ParseUri(new Uri(repoURL));
                        // we got a repository value the author specified in the metadata - so no further
                        // processing needed
                        if (gitPURL != null)
                        {
                            mapping.Add(gitPURL, 1.0F);
                            return mapping;
                        }
                    }
                }
                catch (KeyNotFoundException) { /* continue onwards */ }
                catch (UriFormatException) {  /* the uri specified in the metadata invalid */ }
            }

            return mapping;
        }

        private static readonly List<string> npm_internal_modules = new List<string>()
        {
            "assert",
            "async_hooks",
            "buffer",
            "child_process",
            "cluster",
            "console",
            "constants",
            "crypto",
            "dgram",
            "dns",
            "domain",
            "events",
            "fs",
            "http",
            "http2",
            "https",
            "inspector",
            "module",
            "net",
            "os",
            "path",
            "perf_hooks",
            "process",
            "punycode",
            "querystring",
            "readline",
            "repl",
            "stream",
            "string_decoder",
            "timers",
            "tls",
            "trace_events",
            "tty",
            "url",
            "util",
            "v8",
            "vm",
            "zlib"
        };
    }
}