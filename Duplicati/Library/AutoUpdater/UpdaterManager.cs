﻿//  Copyright (C) 2014, Kenneth Skovhede

//  http://www.hexad.dk, opensource@hexad.dk
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using System.Linq;
using System.Collections.Generic;

namespace Duplicati.Library.AutoUpdater
{
    public enum AutoUpdateStrategy
    {
        CheckBefore,
        CheckDuring,
        CheckAfter,
        InstallBefore,
        InstallDuring,
        InstallAfter,
        Never
    }

    public class UpdaterManager
    {
        private System.Security.Cryptography.RSACryptoServiceProvider m_key;
        private string[] m_urls;
        private string m_appname;
        private string m_installdir;

        public static bool RequiresRespawn { get; set; }

        private KeyValuePair<string, UpdateInfo>? m_hasUpdateInstalled;

        private UpdateInfo m_selfVersion;

        public event Action<Exception> OnError;

        private const string DATETIME_FORMAT = "yyyymmddhhMMss";
        private const string INSTALLDIR_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_INSTALL_ROOT";
        private const string RUN_UPDATED_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_LOAD_UPDATE";
        private const string SLEEP_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_SLEEP";
        private const string UPDATE_MANIFEST_FILENAME = "autoupdate.manifest";

        public const string AUTO_UPDATE_OPTION = "auto-update-strategy";

        /// <summary>
        /// Gets the original directory that this application was installed into
        /// </summary>
        /// <value>The original directory that this application was installed into</value>
        public string InstalledBaseDir
        {
            get
            {
                var s = System.Environment.GetEnvironmentVariable(string.Format(INSTALLDIR_ENVNAME_TEMPLATE, m_appname));
                if (string.IsNullOrWhiteSpace(s))
                    return System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
                else
                    return s;
            }
        }
            
        public UpdaterManager(string[] urls, System.Security.Cryptography.RSACryptoServiceProvider key, string appname, string installdir = null)
        {
            m_key = key;
            m_urls = urls;
            m_appname = appname;
            m_installdir = installdir;
            if (string.IsNullOrWhiteSpace(m_installdir))
            {
                var attempts = new string[] {
                    System.IO.Path.Combine(InstalledBaseDir, "updates"),
                    System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), m_appname, "updates"),
                    System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), m_appname, "updates"),
                    System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), m_appname, "updates"),
                };

                foreach(var p in attempts)
                    if (TestDirectoryIsWriteable(p))
                    {
                        m_installdir = p;
                        break;
                    }
            }
        }

        private Version TryParseVersion(string str)
        {
            Version v;
            if (Version.TryParse(str, out v))
                return v;
            else
                return new Version(0, 0);
        }

        public bool HasUpdateInstalled
        {
            get
            {
                if (!m_hasUpdateInstalled.HasValue)
                {
                    var selfversion = TryParseVersion(this.SelfVersion.Version);

                    m_hasUpdateInstalled = 
                        (from n in FindInstalledVersions()
                            let nversion = TryParseVersion(n.Value.Version)
                            let newerVersion = selfversion < nversion
                            where newerVersion && VerifyUnpackedFolder(n.Key, n.Value)
                            orderby nversion descending
                            select n)
                            .FirstOrDefault();
                }

                return m_hasUpdateInstalled.Value.Value != null;
            }
        }

        private UpdateInfo SelfVersion
        {
            get
            {
                if (m_selfVersion == null)
                {
                    try
                    {
                        m_selfVersion = ReadInstalledManifest(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location));
                    }
                    catch
                    {
                    }

                    if (m_selfVersion == null)
                        m_selfVersion = new UpdateInfo() {
                            Displayname = "Current",
                            Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                            ReleaseTime = new DateTime(0),
                            ReleaseType = 
#if DEBUG
                                "Debug"
#else
                                "Nightly"                           
#endif
                        };
                }

                return m_selfVersion;
            }
        }

        private bool TestDirectoryIsWriteable(string path)
        {
            var p2 = System.IO.Path.Combine(path, "test-" + DateTime.UtcNow.ToString(DATETIME_FORMAT));
            var probe = System.IO.Directory.Exists(path) ? p2 : path;

            if (!System.IO.Directory.Exists(probe))
            {
                try 
                {
                    System.IO.Directory.CreateDirectory(probe);
                    if (probe != path)
                        System.IO.Directory.Delete(probe);       
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        public UpdateInfo CheckForUpdate()
        {
            foreach(var url in m_urls)
            {
                try
                {
                    using(var tmpfile = new Library.Utility.TempFile())
                    {
                        System.Net.WebClient wc = new System.Net.WebClient();
                        wc.DownloadFile(url, tmpfile);

                        using(var fs = System.IO.File.OpenRead(tmpfile))
                        using(var ss = new SignatureReadingStream(fs, m_key))
                        using(var tr = new System.IO.StreamReader(ss))
                        using(var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        {
                            var update = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);

                            if (TryParseVersion(update.Version) <= TryParseVersion(SelfVersion.Version))
                                return null;

                            if (string.Equals(SelfVersion.ReleaseType, "Debug", StringComparison.InvariantCultureIgnoreCase) && !string.Equals(update.ReleaseType, SelfVersion.ReleaseType, StringComparison.CurrentCultureIgnoreCase))
                                return null;

                            return update;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            return null;
        }

        private UpdateInfo ReadInstalledManifest(string folder)
        {
            var manifest = System.IO.Path.Combine(folder, UPDATE_MANIFEST_FILENAME);
            if (System.IO.File.Exists(manifest))
            {
                try
                {
                    using(var fs = System.IO.File.OpenRead(manifest))
                    using(var ss = new SignatureReadingStream(fs, m_key))
                    using(var tr = new System.IO.StreamReader(ss))
                    using(var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        return new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            return null;
        }

        public IEnumerable<KeyValuePair<string, UpdateInfo>> FindInstalledVersions()
        {
            var res = new List<KeyValuePair<string, UpdateInfo>>();
            foreach(var folder in System.IO.Directory.GetDirectories(m_installdir))
            {
                var r = ReadInstalledManifest(folder);
                if (r != null)
                    res.Add(new KeyValuePair<string, UpdateInfo>(folder, r));
            }

            return res;
        }

        public bool DownloadAndUnpackUpdate(UpdateInfo version)
        {
            using(var tempfile = new Library.Utility.TempFile())
            {
                foreach(var url in version.RemoteURLS)
                {
                    try
                    {
                        System.Net.WebClient wc = new System.Net.WebClient();
                        wc.DownloadFile(url, tempfile);

                        var sha256 = System.Security.Cryptography.SHA256.Create();
                        var md5 =  System.Security.Cryptography.MD5.Create();

                        using(var s = System.IO.File.OpenRead(tempfile))
                        {
                            if (s.Length != version.CompressedSize)
                                throw new Exception(string.Format("Invalid file size {0}, expected {1} for {2}", s.Length, version.CompressedSize, url));
                            
                            var sha256hash = Convert.ToBase64String(sha256.ComputeHash(s));
                            if (sha256hash != version.SHA256)
                                throw new Exception(string.Format("Damaged or corrupted file, sha256 mismatch for {0}", url));
                        }

                        using(var s = System.IO.File.OpenRead(tempfile))
                        {
                            var md5hash = Convert.ToBase64String(md5.ComputeHash(s));
                            if (md5hash != version.MD5)
                                throw new Exception(string.Format("Damaged or corrupted file, md5 mismatch for {0}", url));
                        }
                        
                        using(var tempfolder = new Duplicati.Library.Utility.TempFolder())
                        using(var zip = new Duplicati.Library.Compression.FileArchiveZip(tempfile, new Dictionary<string, string>()))
                        {
                            foreach(var file in zip.ListFilesWithSize(""))
                            {
                                if (System.IO.Path.IsPathRooted(file.Key) || file.Key.Trim().StartsWith("..", StringComparison.InvariantCultureIgnoreCase))
                                    throw new Exception(string.Format("Out-of-place file path detected: {0}", file.Key));

                                var targetpath = System.IO.Path.Combine(tempfolder, file.Key);
                                var targetfolder = System.IO.Path.GetDirectoryName(targetpath);
                                if (!System.IO.Directory.Exists(targetfolder))
                                    System.IO.Directory.CreateDirectory(targetfolder);

                                using(var zs = zip.OpenRead(file.Key))
                                using(var fs = System.IO.File.Create(targetpath))
                                    zs.CopyTo(fs);
                            }

                            if (VerifyUnpackedFolder(tempfolder, version))
                            {
                                var targetfolder = System.IO.Path.Combine(m_installdir, version.ReleaseTime.ToString(DATETIME_FORMAT));
                                if (System.IO.Directory.Exists(targetfolder))
                                    System.IO.Directory.Delete(targetfolder, true);
                                
                                System.IO.Directory.CreateDirectory(targetfolder);

                                var tempfolderpath = Duplicati.Library.Utility.Utility.AppendDirSeparator(tempfolder);
                                var tempfolderlength = tempfolderpath.Length;

                                // Would be nice, but does not work :(
                                //System.IO.Directory.Move(tempfolder, targetfolder);

                                foreach(var e in Duplicati.Library.Utility.Utility.EnumerateFileSystemEntries(tempfolder))
                                {
                                    var relpath = e.Substring(tempfolderlength);
                                    if (string.IsNullOrWhiteSpace(relpath))
                                        continue;

                                    var fullpath = System.IO.Path.Combine(targetfolder, relpath);
                                    if (relpath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                                        System.IO.Directory.CreateDirectory(fullpath);
                                    else
                                        System.IO.File.Copy(e, fullpath);
                                }

                                // Verification will kick in when we list the installed updates
                                //VerifyUnpackedFolder(targetfolder, version);

                                m_hasUpdateInstalled = null;
                                return true;
                            }
                            else
                            {
                                throw new Exception(string.Format("Unable to verify unpacked folder for url: {0}", url));
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        if (OnError != null)
                            OnError(ex);
                    }
                }
            }

            return false;
        }

        public bool VerifyUnpackedFolder(string folder, UpdateInfo version = null)
        {
            try
            {
                UpdateInfo update;
                FileEntry manifest;

                var sha256 = System.Security.Cryptography.SHA256.Create();
                var md5 = System.Security.Cryptography.MD5.Create();

                using(var fs = System.IO.File.OpenRead(System.IO.Path.Combine(folder, UPDATE_MANIFEST_FILENAME)))
                {
                    using(var ss = new SignatureReadingStream(fs, m_key))
                    using(var tr = new System.IO.StreamReader(ss))
                    using(var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        update = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);

                    sha256.Initialize();
                    md5.Initialize();

                    fs.Position = 0;
                    var h1 = Convert.ToBase64String(sha256.ComputeHash(fs));
                    fs.Position = 0;
                    var h2 = Convert.ToBase64String(md5.ComputeHash(fs));

                    manifest = new FileEntry() {
                        Path = UPDATE_MANIFEST_FILENAME,
                        Ignore = false,
                        LastWriteTime = update.ReleaseTime,
                        SHA256 = h1,
                        MD5 = h2
                    };
                }

                if (version != null && (update.Displayname != version.Displayname || update.ReleaseTime != version.ReleaseTime))
                    throw new Exception("The found version was not the expected version");

                var paths = update.Files.Where(x => !x.Ignore).ToDictionary(x => x.Path.Replace('/', System.IO.Path.DirectorySeparatorChar), Library.Utility.Utility.ClientFilenameStringComparer);
                paths.Add(manifest.Path, manifest);

                var ignores = (from x in update.Files where x.Ignore select Library.Utility.Utility.AppendDirSeparator(x.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))).ToList();

                folder = Library.Utility.Utility.AppendDirSeparator(folder);
                var baselen = folder.Length;

                foreach(var file in Library.Utility.Utility.EnumerateFileSystemEntries(folder))
                {
                    var relpath = file.Substring(baselen);
                    if (string.IsNullOrWhiteSpace(relpath))
                        continue;

                    FileEntry fe;
                    if (!paths.TryGetValue(relpath, out fe))
                    {
                        var ignore = false;
                        foreach(var c in ignores)
                            if (ignore = relpath.StartsWith(c))
                                break;

                        if (ignore)
                            continue;

                        throw new Exception(string.Format("Found unexpected file: {0}", file));
                    }

                    paths.Remove(relpath);

                    if (fe.Path.EndsWith("/"))
                        continue;

                    sha256.Initialize();
                    md5.Initialize();

                    using(var fs = System.IO.File.OpenRead(file))
                    {
                        if (Convert.ToBase64String(sha256.ComputeHash(fs)) != fe.SHA256)
                            throw new Exception(string.Format("Invalid sha256 hash for file: {0}", file));

                        fs.Position = 0;
                        if (Convert.ToBase64String(md5.ComputeHash(fs)) != fe.MD5)
                            throw new Exception(string.Format("Invalid md5 hash for file: {0}", file));
                    }
                }

                var filteredpaths = (from p in paths
                        where !string.IsNullOrWhiteSpace(p.Key) && !p.Key.EndsWith("/")
                        select p.Key).ToList();


                if (filteredpaths.Count == 1)
                    throw new Exception(string.Format("Folder {0} is missing: {1}", folder, filteredpaths.First()));
                else if (filteredpaths.Count > 0)
                    throw new Exception(string.Format("Folder {0} is missing {1} files", folder, filteredpaths.Count));

                return true;
            }
            catch (Exception ex)
            {
                if (OnError != null)
                    OnError(ex);
            }

            return false;
        }

        public bool SetRunUpdate()
        {
            if (HasUpdateInstalled)
            {
                Environment.SetEnvironmentVariable(string.Format(RUN_UPDATED_ENVNAME_TEMPLATE, m_appname), m_hasUpdateInstalled.Value.Key);
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(string.Format(RUN_UPDATED_ENVNAME_TEMPLATE, m_appname)));
            }

            return false;
        }

        public void CreateUpdatePackage(System.Security.Cryptography.RSACryptoServiceProvider key, string inputfolder, string outputfolder, string manifest = null)
        {
            // Read the existing manifest

            UpdateInfo remoteManifest;

            var manifestpath = manifest ?? System.IO.Path.Combine(inputfolder, UPDATE_MANIFEST_FILENAME);

            using(var s = System.IO.File.OpenRead(manifestpath))
            using(var sr = new System.IO.StreamReader(s))
            using(var jr = new Newtonsoft.Json.JsonTextReader(sr))
                remoteManifest = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);
            
            if (remoteManifest.Files == null)
                remoteManifest.Files = new FileEntry[0];

            if (remoteManifest.ReleaseTime.Ticks == 0)
                remoteManifest.ReleaseTime = DateTime.UtcNow;

            var ignoreFiles = (from n in remoteManifest.Files
                                        where n.Ignore
                                        select n).ToArray();

            var ignoreMap = ignoreFiles.ToDictionary(k => k.Path, k => "", Duplicati.Library.Utility.Utility.ClientFilenameStringComparer);

            remoteManifest.MD5 = null;
            remoteManifest.SHA256 = null;
            remoteManifest.Files = null;
            remoteManifest.UncompressedSize = 0;

            var localManifest = remoteManifest.Clone();
            localManifest.RemoteURLS = null;

            inputfolder = Duplicati.Library.Utility.Utility.AppendDirSeparator(inputfolder);
            var baselen = inputfolder.Length;
            var dirsep = System.IO.Path.DirectorySeparatorChar.ToString();

            ignoreMap.Add(UPDATE_MANIFEST_FILENAME, "");

            var md5 = System.Security.Cryptography.MD5.Create();
            var sha256 = System.Security.Cryptography.SHA256.Create();

            Func<string, string> computeMD5 = (path) =>
            {
                md5.Initialize();
                using(var fs = System.IO.File.OpenRead(path))
                    return Convert.ToBase64String(md5.ComputeHash(fs));
            };

            Func<string, string> computeSHA256 = (path) =>
            {
                sha256.Initialize();
                using(var fs = System.IO.File.OpenRead(path))
                    return Convert.ToBase64String(sha256.ComputeHash(fs));
            };

            // Build a zip
            using (var archive_temp = new Duplicati.Library.Utility.TempFile())
            {
                using (var zipfile = new Duplicati.Library.Compression.FileArchiveZip(archive_temp, new Dictionary<string, string>()))
                {
                    Func<string, string, bool> addToArchive = (path, relpath) =>
                    {
                        if (ignoreMap.ContainsKey(relpath))
                            return false;
                    
                        if (path.EndsWith(dirsep))
                            return true;

                        using (var source = System.IO.File.OpenRead(path))
                        using (var target = zipfile.CreateFile(relpath, 
                                           Duplicati.Library.Interface.CompressionHint.Compressible,
                                           System.IO.File.GetLastAccessTimeUtc(path)))
                        {
                            source.CopyTo(target);
                            remoteManifest.UncompressedSize += source.Length;
                        }

                        return true;
                    };
                        
                    // Build the update manifest
                    localManifest.Files =
                (from fse in Duplicati.Library.Utility.Utility.EnumerateFileSystemEntries(inputfolder)
                                let relpath = fse.Substring(baselen)
                                where addToArchive(fse, relpath)
                                select new FileEntry() {
                        Path = relpath,
                        LastWriteTime = System.IO.File.GetLastAccessTimeUtc(fse),
                        MD5 = fse.EndsWith(dirsep) ? null : computeMD5(fse),
                        SHA256 = fse.EndsWith(dirsep) ? null : computeSHA256(fse)
                    })
                .Union(ignoreFiles).ToArray();

                    // Write a signed manifest with the files
                
                        using (var ms = new System.IO.MemoryStream())
                        using (var sw = new System.IO.StreamWriter(ms))
                        {
                            new Newtonsoft.Json.JsonSerializer().Serialize(sw, localManifest);
                            sw.Flush();

                            using (var ms2 = new System.IO.MemoryStream())
                            {
                                SignatureReadingStream.CreateSignedStream(ms, ms2, key);
                                ms2.Position = 0;
                                using (var sigfile = zipfile.CreateFile(UPDATE_MANIFEST_FILENAME, 
                                    Duplicati.Library.Interface.CompressionHint.Compressible,
                                    DateTime.UtcNow))
                                    ms2.CopyTo(sigfile);

                            }
                        }
                }

                remoteManifest.CompressedSize = new System.IO.FileInfo(archive_temp).Length;
                remoteManifest.MD5 = computeMD5(archive_temp);
                remoteManifest.SHA256 = computeSHA256(archive_temp);

                System.IO.File.Move(archive_temp, System.IO.Path.Combine(outputfolder, "package.zip"));

            }

            // Write a signed manifest for upload

            using(var tf = new Duplicati.Library.Utility.TempFile())
            {
                using (var ms = new System.IO.MemoryStream())
                using (var sw = new System.IO.StreamWriter(ms))
                {
                    new Newtonsoft.Json.JsonSerializer().Serialize(sw, remoteManifest);
                    sw.Flush();

                    using (var fs = System.IO.File.OpenWrite(tf))
                        SignatureReadingStream.CreateSignedStream(ms, fs, key);
                }

                System.IO.File.Move(tf, System.IO.Path.Combine(outputfolder, UPDATE_MANIFEST_FILENAME));
            }

        }

        private int RunMethod(System.Reflection.MethodInfo method, string[] args)
        {
            try
            {
                var n = method.Invoke(null, new object[] { args });
                if (method.ReturnType == typeof(int))
                    return (int)n;

                return 0;
            } 
            catch (System.Reflection.TargetInvocationException tex)
            {
                if (tex.InnerException != null)
                    throw tex.InnerException;
                else
                    throw;
            }
        }

        public int RunFromMostRecent(System.Reflection.MethodInfo method, string[] cmdargs, AutoUpdateStrategy defaultstrategy = AutoUpdateStrategy.InstallDuring)
        {
            // If we are not the primary domain, just execute
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                return RunMethod(method, cmdargs);

            // If we are a re-launch, wait briefly for the other process to exit
            var sleepmarker = System.Environment.GetEnvironmentVariable(string.Format(SLEEP_ENVNAME_TEMPLATE, m_appname));
            if (!string.IsNullOrWhiteSpace(sleepmarker))
            {
                System.Environment.SetEnvironmentVariable(string.Format(SLEEP_ENVNAME_TEMPLATE, m_appname), null);
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(15));
            }

            var options = Duplicati.Library.Utility.CommandLineParser.ExtractOptions(new List<string>(cmdargs), null);
            string optstr;
            AutoUpdateStrategy strategy;
            if (!options.TryGetValue(AUTO_UPDATE_OPTION, out optstr) || !Enum.TryParse(optstr, out strategy))
                strategy = defaultstrategy;


            System.Threading.Thread backgroundChecker = null;
            UpdateInfo updateDetected = null;
            bool updateInstalled = false;

            bool checkForUpdate;
            bool downloadUpdate;
            bool runAfter;
            bool runDuring;
            bool runBefore;


            switch (strategy)
            {
                case AutoUpdateStrategy.CheckBefore:
                case AutoUpdateStrategy.CheckDuring:
                case AutoUpdateStrategy.CheckAfter:
                    checkForUpdate = true;
                    downloadUpdate = false;
                    break;

                case AutoUpdateStrategy.InstallBefore:
                case AutoUpdateStrategy.InstallDuring:
                case AutoUpdateStrategy.InstallAfter:
                    checkForUpdate = true;
                    downloadUpdate = true;
                    break;

                default:
                    checkForUpdate = false;
                    downloadUpdate = false;
                    break;
            }

            switch (strategy)
            {
                case AutoUpdateStrategy.CheckBefore:
                case AutoUpdateStrategy.InstallBefore:
                    runBefore = true;
                    runDuring = false;
                    runAfter = false;
                    break;

                case AutoUpdateStrategy.CheckAfter:
                case AutoUpdateStrategy.InstallAfter:
                    runBefore = false;
                    runDuring = false;
                    runAfter = true;
                    break;

                case AutoUpdateStrategy.CheckDuring:
                case AutoUpdateStrategy.InstallDuring:
                    runBefore = false;
                    runDuring = true;
                    runAfter = false;
                    break;

                default:
                    runBefore = false;
                    runDuring = false;
                    runAfter = false;
                    break;
            }

            if (checkForUpdate)
            {
                backgroundChecker = new System.Threading.Thread(() =>
                {
                    // Don't run "during" if the task is short
                    if (runDuring)
                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));

                    updateDetected = CheckForUpdate();
                    if (updateDetected != null && downloadUpdate)
                    {
                        if (!runDuring)
                            Console.WriteLine("Update to {0} detected, installing...", updateDetected.Displayname);
                        updateInstalled = DownloadAndUnpackUpdate(updateDetected);
                    }
                });

                backgroundChecker.IsBackground = true;
                backgroundChecker.Name = "BackgroundUpdateChecker";

                if (!runAfter)
                    backgroundChecker.Start();

                if (runBefore)
                {
                    Console.WriteLine("Checking for update ...");
                    backgroundChecker.Join();

                    if (downloadUpdate)
                    {
                        if (updateInstalled)
                            Console.WriteLine("Install succeeded, running updated version");
                        else
                            Console.WriteLine("Install or download failed, using current version");
                    }
                    else if (updateDetected != null)
                    {
                        Console.WriteLine("Update \"{0}\" detected", updateDetected.Displayname);
                    }

                    backgroundChecker = null;
                }
            }
            
            // Check if there are updates installed, otherwise use current
            var best = HasUpdateInstalled ? m_hasUpdateInstalled.Value : new KeyValuePair<string, UpdateInfo>(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, SelfVersion);
            Environment.SetEnvironmentVariable(string.Format(INSTALLDIR_ENVNAME_TEMPLATE, m_appname), InstalledBaseDir);

            var folder = best.Key;

            // Basic idea with the loop is that the running AppDomain can use 
            // RUN_UPDATED_ENVNAME_TEMPLATE to signal that a new version is ready
            // when the caller exits, the new update is executed
            //
            // This allows more or less seamless updates
            //
            // The client is responsible for checking for updates and starting the downloads
            //

            int result = 0;
            while (!string.IsNullOrWhiteSpace(folder) && System.IO.Directory.Exists(folder))
            {
                var prevfolder = folder;
                // Create the new domain
                var domain = AppDomain.CreateDomain(
                                 "UpdateDomain",
                                 null,
                                 folder,
                                 "",
                                 false
                             );

                result = domain.ExecuteAssemblyByName(method.DeclaringType.Assembly.GetName().Name, cmdargs);

                try { AppDomain.Unload(domain); }
                catch (Exception ex)
                { 
                    Console.WriteLine("Appdomain unload error: {0}", ex);
                }

                folder = Environment.GetEnvironmentVariable(string.Format(RUN_UPDATED_ENVNAME_TEMPLATE, m_appname));
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Environment.SetEnvironmentVariable(string.Format(RUN_UPDATED_ENVNAME_TEMPLATE, m_appname), null);
                    if (!VerifyUnpackedFolder(folder))
                        folder = prevfolder; //Go back and run the previous version
                    else if (RequiresRespawn)
                    {
                        // We have a valid update, and the current instance is terminated.
                        // But due to external libraries, we need to re-spawn the original process

                        try
                        {
                            var args = Environment.CommandLine;
                            var app = Environment.GetCommandLineArgs().First();
                            args = args.Substring(app.Length);

                            if (!System.IO.Path.IsPathRooted(app))
                                app = System.IO.Path.Combine(InstalledBaseDir, app);

                            // Re-launch but give the OS a little time to fully unload all open handles, etc.                        
                            var si = new System.Diagnostics.ProcessStartInfo(app, args);
                            si.EnvironmentVariables.Add(string.Format(SLEEP_ENVNAME_TEMPLATE, m_appname), "1");
                            si.UseShellExecute = false;

                            var pr = System.Diagnostics.Process.Start(si);

                            if (pr.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
                                folder = prevfolder;
                            else
                                return 0;
                        }
                        catch (Exception ex)
                        {
                            if (OnError != null)
                                OnError(ex);
                            folder = prevfolder;
                        }
                    }
                }
            }

            if (backgroundChecker != null && runAfter)
            {
                Console.WriteLine("Checking for update ...");

                backgroundChecker.Start();
                backgroundChecker.Join();
            }

            if (backgroundChecker != null && updateDetected != null)
            {
                if (backgroundChecker.IsAlive)
                {
                    Console.WriteLine("Waiting for update \"{0}\" to complete", updateDetected.Displayname);
                    backgroundChecker.Join();
                }

                if (downloadUpdate)
                {
                    if (updateInstalled)
                        Console.WriteLine("Install succeeded, running updated version on next launch");
                    else
                        Console.WriteLine("Install or download failed, using current version on next launch");
                }
                else
                {
                    Console.WriteLine("Update \"{0}\" detected", updateDetected.Displayname);
                }
            }

            return result;
        }

    }
}

