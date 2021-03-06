// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Bind.Databases
{
    using System;
    using System.Collections;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using WixToolset.Cab;
    using WixToolset.Data;
    using WixToolset.Data.Rows;

    /// <summary>
    /// Builds cabinets using multiple threads. This implements a thread pool that generates cabinets with multiple
    /// threads. Unlike System.Threading.ThreadPool, it waits until all threads are finished.
    /// </summary>
    internal sealed class CabinetBuilder
    {
        private Queue cabinetWorkItems;
        private object lockObject;
        private int threadCount;

        // Address of Binder's callback function for Cabinet Splitting
        private IntPtr newCabNamesCallBackAddress;

        public int MaximumCabinetSizeForLargeFileSplitting { get; set; }
        public int MaximumUncompressedMediaSize { get; set; }

        /// <summary>
        /// Instantiate a new CabinetBuilder.
        /// </summary>
        /// <param name="threadCount">number of threads to use</param>
        /// <param name="newCabNamesCallBackAddress">Address of Binder's callback function for Cabinet Splitting</param>
        public CabinetBuilder(int threadCount, IntPtr newCabNamesCallBackAddress)
        {
            if (0 >= threadCount)
            {
                throw new ArgumentOutOfRangeException("threadCount");
            }

            this.cabinetWorkItems = new Queue();
            this.lockObject = new object();

            this.threadCount = threadCount;

            // Set Address of Binder's callback function for Cabinet Splitting
            this.newCabNamesCallBackAddress = newCabNamesCallBackAddress;
        }

        /// <summary>
        /// Enqueues a CabinetWorkItem to the queue.
        /// </summary>
        /// <param name="cabinetWorkItem">cabinet work item</param>
        public void Enqueue(CabinetWorkItem cabinetWorkItem)
        {
            this.cabinetWorkItems.Enqueue(cabinetWorkItem);
        }

        /// <summary>
        /// Create the queued cabinets.
        /// </summary>
        /// <returns>error message number (zero if no error)</returns>
        public void CreateQueuedCabinets()
        {
            // don't create more threads than the number of cabinets to build
            if (this.cabinetWorkItems.Count < this.threadCount)
            {
                this.threadCount = this.cabinetWorkItems.Count;
            }

            if (0 < this.threadCount)
            {
                Thread[] threads = new Thread[this.threadCount];

                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(new ThreadStart(this.ProcessWorkItems));
                    threads[i].Start();
                }

                // wait for all threads to finish
                foreach (Thread thread in threads)
                {
                    thread.Join();
                }
            }
        }

        /// <summary>
        /// This function gets called by multiple threads to do actual work.
        /// It takes one work item at a time and calls this.CreateCabinet().
        /// It does not return until cabinetWorkItems queue is empty
        /// </summary>
        private void ProcessWorkItems()
        {
            try
            {
                while (true)
                {
                    CabinetWorkItem cabinetWorkItem;

                    lock (this.cabinetWorkItems)
                    {
                        // check if there are any more cabinets to create
                        if (0 == this.cabinetWorkItems.Count)
                        {
                            break;
                        }

                        cabinetWorkItem = (CabinetWorkItem)this.cabinetWorkItems.Dequeue();
                    }

                    // create a cabinet
                    this.CreateCabinet(cabinetWorkItem);
                }
            }
            catch (WixException we)
            {
                Messaging.Instance.OnMessage(we.Error);
            }
            catch (Exception e)
            {
                Messaging.Instance.OnMessage(WixErrors.UnexpectedException(e.Message, e.GetType().ToString(), e.StackTrace));
            }
        }

        /// <summary>
        /// Creates a cabinet using the wixcab.dll interop layer.
        /// </summary>
        /// <param name="cabinetWorkItem">CabinetWorkItem containing information about the cabinet to create.</param>
        private void CreateCabinet(CabinetWorkItem cabinetWorkItem)
        {
            Messaging.Instance.OnMessage(WixVerboses.CreateCabinet(cabinetWorkItem.CabinetFile));

            int maxCabinetSize = 0; // The value of 0 corresponds to default of 2GB which means no cabinet splitting
            ulong maxPreCompressedSizeInBytes = 0;

            if (MaximumCabinetSizeForLargeFileSplitting != 0)
            {
                // User Specified Max Cab Size for File Splitting, So Check if this cabinet has a single file larger than MaximumUncompressedFileSize
                // If a file is larger than MaximumUncompressedFileSize, then the cabinet containing it will have only this file
                if (1 == cabinetWorkItem.FileFacades.Count())
                {
                    // Cabinet has Single File, Check if this is Large File than needs Splitting into Multiple cabs
                    // Get the Value for Max Uncompressed Media Size
                    maxPreCompressedSizeInBytes = (ulong)MaximumUncompressedMediaSize * 1024 * 1024;

                    foreach (FileFacade facade in cabinetWorkItem.FileFacades) // No other easy way than looping to get the only row
                    {
                        if ((ulong)facade.File.FileSize >= maxPreCompressedSizeInBytes)
                        {
                            // If file is larger than MaximumUncompressedFileSize set Maximum Cabinet Size for Cabinet Splitting
                            maxCabinetSize = MaximumCabinetSizeForLargeFileSplitting;
                        }
                    }
                }
            }

            // create the cabinet file
            string cabinetFileName = Path.GetFileName(cabinetWorkItem.CabinetFile);
            string cabinetDirectory = Path.GetDirectoryName(cabinetWorkItem.CabinetFile);

            using (WixCreateCab cab = new WixCreateCab(cabinetFileName, cabinetDirectory, cabinetWorkItem.FileFacades.Count(), maxCabinetSize, cabinetWorkItem.MaxThreshold, cabinetWorkItem.CompressionLevel))
            {
                foreach (FileFacade facade in cabinetWorkItem.FileFacades)
                {
                    cab.AddFile(facade);
                }

                cab.Complete(newCabNamesCallBackAddress);
            }
        }
    }
}

