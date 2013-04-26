﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.PythonTools.Analysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AnalyzerStatusTests {
    [TestClass]
    public class UpdaterTests {
        [TestMethod, Priority(0)]
        public void InitializeWithoutCrashing() {
            using (var updater = new AnalyzerStatusUpdater("hi")) { }

            using (var updater = new AnalyzerStatusListener(x => { })) { }
        }

        [TestMethod, Priority(0)]
        public void SendUpdates() {
            Dictionary<string, AnalysisProgress> results = null;
            AutoResetEvent ready = new AutoResetEvent(false);

            using (var listener = new AnalyzerStatusListener(r => { results = r; ready.Set(); })) {
                ready.Reset();
                listener.RequestUpdate();
                ready.WaitOne();
                Assert.IsNotNull(results);
                Assert.AreEqual(0, results.Count);

                using (var sender1 = new AnalyzerStatusUpdater("s1"))
                using (var sender2 = new AnalyzerStatusUpdater("s2")) {
                    // No way to block on initialization
                    Thread.Sleep(100);

                    ready.Reset();
                    listener.RequestUpdate();
                    ready.WaitOne();
                    Assert.IsNotNull(results);
                    Assert.AreEqual(2, results.Count);
                    Assert.IsTrue(results.ContainsKey("s1"), "s1 not found in {0}" + string.Join(", ", results.Keys));
                    Assert.IsTrue(results.ContainsKey("s2"), "s2 not found in {0}" + string.Join(", ", results.Keys));
                    Assert.AreEqual(int.MaxValue, results["s1"].Progress, "s1.Progress not initialized to MaxValue");
                    Assert.AreEqual(0, results["s1"].Maximum, "s1.Maximum not initialized to 0");
                    Assert.AreEqual(int.MaxValue, results["s2"].Progress, "s2.Progress not initialized to MaxValue");
                    Assert.AreEqual(0, results["s2"].Maximum, "s2.Maximum not initialized to 0");

                    sender1.UpdateStatus(100, 200);
                    // No way to block on sending an update
                    Thread.Sleep(50);

                    ready.Reset();
                    listener.RequestUpdate();
                    ready.WaitOne();
                    Assert.AreEqual(100, results["s1"].Progress, "s1.Progress not set to 100");
                    Assert.AreEqual(200, results["s1"].Maximum, "s1.Maximum not set to 200");
                    Assert.AreEqual(int.MaxValue, results["s2"].Progress, "s2.Progress changed from MaxValue");
                    Assert.AreEqual(0, results["s2"].Maximum, "s2.Maximum changed from 0");

                    sender2.UpdateStatus(1000, 2000);
                    // No way to block on sending an update
                    Thread.Sleep(50);

                    ready.Reset();
                    listener.RequestUpdate();
                    ready.WaitOne();
                    Assert.AreEqual(100, results["s1"].Progress, "s1.Progress changed from 100");
                    Assert.AreEqual(200, results["s1"].Maximum, "s1.Maximum changed from 200");
                    Assert.AreEqual(1000, results["s2"].Progress, "s2.Progress not set to 1000");
                    Assert.AreEqual(2000, results["s2"].Maximum, "s2.Maximum not set to 2000");
                }

                ready.Reset();
                listener.RequestUpdate();
                ready.WaitOne();
                Assert.IsNotNull(results);
                Assert.AreEqual(0, results.Count, "results were not cleaned up");
            }
        }

        [TestMethod, Priority(0)]
        public void LotsOfUpdaters() {
            var updaters = new List<AnalyzerStatusUpdater>();

            for (int i = 0; i < 1; ++i) {
                // We should stop creating new entries well before 1000
                for (int j = 0; j < 1000; ++j) {
                    var newUpdater = new AnalyzerStatusUpdater("S" + j.ToString());
                    updaters.Add(newUpdater);
                }
                // Give the updaters a chance to start
                Thread.Sleep(1000);

                // Make sure that we got failures.
                try {
                    foreach (var u in updaters) {
                        u.ThrowPendingExceptions();
                    }
                    Assert.Fail("Should not have been able to create 1000 updaters");
                } catch (InvalidOperationException) {
                }
                foreach (var u in updaters) {
                    u.Dispose();
                }
                updaters.Clear();
            }
        }
    }
}