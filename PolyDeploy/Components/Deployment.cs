﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Cantarus.Modules.PolyDeploy.Components
{
    internal class Deployment
    {
        protected string IntakePath
        {
            get
            {
                return Path.Combine(WorkingPath, "intake");
            }
        }

        protected string ModulesPath
        {
            get
            {
                return Path.Combine(WorkingPath, "modules");
            }
        }

        protected string TempPath
        {
            get
            {
                return Path.Combine(WorkingPath, "temp");
            }
        }

        protected string IPAddress { get; set; }
        protected string WorkingPath { get; set; }
        protected List<string> PackageZips { get; set; }

        public Deployment(string ipAddress)
        {
            // Store ip address for logging later.
            IPAddress = ipAddress;

            // Generate a temporary directory.
            WorkingPath = Utilities.AvailableDirectory();

            // Create working directory if it doesn't exist.
            CreateDirectoryIfNotExist(WorkingPath);

            // Create the intake directory if it doesn't exist.
            CreateDirectoryIfNotExist(IntakePath);

            // Create the modules directory if it doesn't exist.
            CreateDirectoryIfNotExist(ModulesPath);

            // Create the temporary directory if it doesn't exist.
            CreateDirectoryIfNotExist(TempPath);
        }

        public Dictionary<string, List<InstallJob>> Deploy()
        {
            // Identify package zips.
            List<string> packageZips = IdentifyPackages();

            // Create install jobs.
            List<InstallJob> installJobs = new List<InstallJob>();
            List<PackageJob> packageJobs = new List<PackageJob>();

            foreach (string packageZip in packageZips)
            {
                InstallJob installJob = new InstallJob(packageZip);
                installJobs.Add(installJob);
                packageJobs.AddRange(installJob.Packages);
            }

            // Are package dependencies fulfulled?
            foreach (InstallJob installJob in installJobs)
            {
                installJob.CheckDependencies(packageJobs);
            }

            // Order jobs.
            SortedList<int, InstallJob> orderedInstall = OrderInstallJobs(installJobs);

            // Do the install.
            List<InstallJob> successJobs = new List<InstallJob>();
            List<InstallJob> failedJobs = new List<InstallJob>();

            foreach (KeyValuePair<int, InstallJob> keyPair in orderedInstall)
            {
                InstallJob job = keyPair.Value;

                if (job.Install())
                {
                    successJobs.Add(job);
                } else
                {
                    failedJobs.Add(job);
                }
            }

            // Log failures.
            LogAnyFailures(successJobs);
            LogAnyFailures(failedJobs);

            Dictionary<string, List<InstallJob>> results = new Dictionary<string, List<InstallJob>>();

            results.Add("Installed", successJobs);
            results.Add("Failed", failedJobs);

            return results;
        }

        protected virtual void LogAnyFailures(List<InstallJob> jobs)
        {
            // Nothing in here yet.
        }

        private SortedList<int, InstallJob> OrderInstallJobs (List<InstallJob> installJobs)
        {
            SortedList<int, InstallJob> orderedInstall = new SortedList<int, InstallJob>();

            foreach (InstallJob ij in installJobs)
            {
                // Already in the list?
                if (!orderedInstall.ContainsValue(ij))
                {
                    // No, add.
                    AddInstallJob(ij, orderedInstall, installJobs);
                }
            }

            return orderedInstall;
        }

        private void AddInstallJob(InstallJob installJob, SortedList<int, InstallJob> orderedInstall, List<InstallJob> installJobs, List<InstallJob> dependencyStack = null)
        {
            // Initialise dependency stack if needed.
            if (dependencyStack == null)
            {
                dependencyStack = new List<InstallJob>();
            }

            // Is this job already in the dependency stack?
            if (dependencyStack.Contains(installJob))
            {
                // Yes, that's a circular dependency detection then.
                throw new Exception("Circular package dependency!");
            }

            // Add this job to the dependency stack.
            dependencyStack.Add(installJob);

            // Loop packages in this install job.
            foreach (PackageJob pj in installJob.Packages)
            {
                // Loop dependencies in this package.
                foreach (PackageDependency pd in pj.Dependencies)
                {
                    // Is this dependency met by our deployment and is it a package dependency?
                    if (pd.DeployMet && pd.Type.Equals("package"))
                    {
                        // Try and find the install job that provides this dependency.
                        InstallJob foundInstallDependency = FindInstallJobWithPackage(pd.Value, installJobs);

                        // Did we find it?
                        if (foundInstallDependency == null)
                        {
                            // No, unfulfilled dependency.
                            throw new Exception("Unfulfilled package dependency.");
                        }

                        // Is it already in the ordered jobs?
                        if (!orderedInstall.ContainsValue(foundInstallDependency))
                        {
                            // No, add that install job first.
                            AddInstallJob(foundInstallDependency, orderedInstall, installJobs, dependencyStack);
                        }
                    }
                }
            }

            // Add ourself.
            orderedInstall.Add(orderedInstall.Count, installJob);
        }

        private InstallJob FindInstallJobWithPackage(string name, List<InstallJob> installJobs)
        {
            foreach (InstallJob ij in installJobs)
            {
                foreach (PackageJob pj in ij.Packages)
                {
                    if (pj.Name.ToLower().Equals(name.ToLower()))
                    {
                        return ij;
                    }
                }
            }

            return null;
        }

        protected List<string> IdentifyPackages()
        {
            return IdentifyPackagesInDirectory(IntakePath);
        }

        protected List<string> IdentifyPackagesInDirectory(string directoryPath)
        {
            List<string> packages = new List<string>();

            // Loop each file in directory.
            foreach (string testPath in Directory.GetFiles(directoryPath))
            {
                // Is it a zip file?
                if (Path.GetExtension(testPath).ToLower().Equals(".zip") && !Path.GetFileNameWithoutExtension(testPath).ToLower().Equals("resources"))
                {
                    // Does it contain a module?
                    if (ZipHasDnnManifest(testPath))
                    {
                        // Yes, add to packages list.
                        packages.Add(testPath);
                    }
                    else
                    {
                        // Does it have other zips?
                        if (ZipHasOtherZip(testPath))
                        {
                            string tempPath = Utilities.AvailableDirectory(TempPath);

                            CreateDirectoryIfNotExist(tempPath);

                            ZipFile.ExtractToDirectory(testPath, tempPath);

                            packages.AddRange(IdentifyPackagesInDirectory(tempPath));
                        }
                    }
                }
            }

            return packages;
        }

        protected bool ZipHasDnnManifest(string filePath)
        {
            return ZipHasFileWithExtension(filePath, ".dnn");
        }

        protected bool ZipHasOtherZip(string filePath)
        {
            return ZipHasFileWithExtension(filePath, ".zip");
        }

        protected bool ZipHasFileWithExtension(string filePath, string extension)
        {
            bool hasFile = false;

            try
            {
                using (FileStream fs = File.OpenRead(filePath))
                {
                    ZipArchive archive = new ZipArchive(fs);

                    // Loop entries in archive.
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (Path.GetExtension(entry.Name).ToLower().Equals(extension))
                        {
                            hasFile = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt zip?
                return false;
            }

            return hasFile;
        }

        protected void CreateDirectoryIfNotExist(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }
    }
}