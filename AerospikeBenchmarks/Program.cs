﻿/* 
 * Copyright 2012-2023 Aerospike, Inc.
 *
 * Portions may be licensed to Aerospike, Inc. under one or more contributor
 * license agreements.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */
using Aerospike.Client;

namespace Aerospike.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                RunBenchmarks();
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
                Console.WriteLine(e.StackTrace);
            }
        }
        private static void RunBenchmarks()
        {
            Log.SetCallback(LogCallback);

            Args args = new Args();
            args.Print();

            Log.Level level = args.debug ? Log.Level.DEBUG : Log.Level.INFO;
            Log.SetLevel(level);

            if (args.sync)
            {
                ClientPolicy policy = new ClientPolicy();
                policy.user = args.user;
                policy.password = args.password;
                policy.tlsPolicy = args.tlsPolicy;
                policy.authMode = args.authMode;
                AerospikeClient client = new AerospikeClient(policy, args.hosts);

                try
                {
					var metricsWrite = new Metrics(Metrics.MetricTypes.Write, args);
					ILatencyManager latencyMgr = args.latency
													? (args.latencyAltFormat
														? new LatencyManagerAlt(args.latencyColumns, args.latencyShift)
														: new LatencyManager(args.latencyColumns, args.latencyShift))
													: null;
					Metrics metricsRead = null;

					if (!args.initialize)
					{
						metricsRead = new Metrics(Metrics.MetricTypes.Read, args);
					}

					args.SetServerSpecific(client);

					if (metricsRead is null)
					{
						var prog = new Initialize(args, metricsWrite, latencyMgr);
						prog.RunSync(client);
					}
					else
					{
						//var prog = new ReadWrite(args, metricsWrite, metricsRead, latencyMgr);
						//prog.RunSync(client);
					}
				}
                finally
                {
                    client.Close();
                }
            }
            else
            {
                AsyncClientPolicy policy = new AsyncClientPolicy();
                policy.user = args.user;
                policy.password = args.password;
                policy.tlsPolicy = args.tlsPolicy;
                policy.authMode = args.authMode;
                policy.asyncMaxCommands = args.commandMax;

                AsyncClient client = new AsyncClient(policy, args.hosts);

                try
                {
					var metricsWrite = new Metrics(Metrics.MetricTypes.Write, args);
					ILatencyManager latencyMgr = args.latency
													? (args.latencyAltFormat
														? new LatencyManagerAlt(args.latencyColumns, args.latencyShift)
														: new LatencyManager(args.latencyColumns, args.latencyShift))
													: null;
					Metrics metricsRead = null;

					if (!args.initialize)
					{
						metricsRead = new Metrics(Metrics.MetricTypes.Read, args);
					}

					args.SetServerSpecific(client);

					if (metricsRead is null)
					{
						var prog = new Initialize(args, metricsWrite, latencyMgr);
						prog.RunAsync(client);
					}
					else
					{
						//var prog = new ReadWrite(args, metricsWrite, metricsRead, latencyMgr);
						//prog.RunAsync(client);
					}
				}
                finally
                {
                    client.Close();
                }
            }

            if (PrefStats.EnableTimings)
            {
                PrefStats.ToCSV(args.LatencyFileCSV);
                PrefStats.ToJson(args.LatencyFileJson);
            }
        }

        private static void LogCallback(Log.Level level, string message)
        {
            Console.WriteLine(level.ToString() + ' ' + message);
        }
    }
}
