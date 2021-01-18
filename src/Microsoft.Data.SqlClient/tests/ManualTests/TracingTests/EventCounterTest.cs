﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Transactions;
using Xunit;

namespace Microsoft.Data.SqlClient.ManualTesting.Tests
{
    /// <summary>
    /// This unit test is just valid for .NetCore 3.0 and above
    /// </summary>
    public class EventCounterTest
    {
        public EventCounterTest()
        {
            ClearConnectionPools();
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public void EventCounter_HardConnectionsCounters_Functional()
        {
            //create a non-pooled connection
            var stringBuilder = new SqlConnectionStringBuilder(DataTestUtility.TCPConnectionString) {Pooling = false};

            using var conn = new SqlConnection(stringBuilder.ToString());

            //initially we have no open physical connections
            Assert.Equal(0, SqlClientEventSourceProps.ActiveHardConnections);
            Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);

            conn.Open();

            //when the connection gets opened, the real physical connection appears
            Assert.Equal(1, SqlClientEventSourceProps.ActiveHardConnections);
            Assert.Equal(1, SqlClientEventSourceProps.NonPooledConnections);

            conn.Close();

            //when the connection gets closed, the real physical connection is also closed
            Assert.Equal(0, SqlClientEventSourceProps.ActiveHardConnections);
            Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public void EventCounter_SoftConnectionsCounters_Functional()
        {
            //create a pooled connection
            var stringBuilder = new SqlConnectionStringBuilder(DataTestUtility.TCPConnectionString) {Pooling = true};

            using (var conn = new SqlConnection(stringBuilder.ToString()))
            {
                //initially we have no open physical connections
                Assert.Equal(0, SqlClientEventSourceProps.ActiveHardConnections);
                Assert.Equal(0, SqlClientEventSourceProps.ActiveSoftConnections);
                Assert.Equal(0, SqlClientEventSourceProps.PooledConnections);
                Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);
                Assert.Equal(0, SqlClientEventSourceProps.ActiveConnectionPools);
                Assert.Equal(0, SqlClientEventSourceProps.ActiveConnections);
                Assert.Equal(0, SqlClientEventSourceProps.FreeConnections);

                conn.Open();

                //when the connection gets opened, the real physical connection appears
                //and the appropriate pooling infrastructure gets deployed
                Assert.Equal(1, SqlClientEventSourceProps.ActiveHardConnections);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveSoftConnections);
                Assert.Equal(1, SqlClientEventSourceProps.PooledConnections);
                Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveConnectionPools);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveConnections);
                Assert.Equal(0, SqlClientEventSourceProps.FreeConnections);

                conn.Close();

                //when the connection gets closed, the real physical connection gets returned to the pool
                Assert.Equal(1, SqlClientEventSourceProps.ActiveHardConnections);
                Assert.Equal(0, SqlClientEventSourceProps.ActiveSoftConnections);
                Assert.Equal(1, SqlClientEventSourceProps.PooledConnections);
                Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveConnectionPools);
                Assert.Equal(0, SqlClientEventSourceProps.ActiveConnections);
                Assert.Equal(1, SqlClientEventSourceProps.FreeConnections);
            }

            using (var conn2 = new SqlConnection(stringBuilder.ToString()))
            {
                conn2.Open();

                //the next open connection will reuse the underlying physical connection
                Assert.Equal(1, SqlClientEventSourceProps.ActiveHardConnections);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveSoftConnections);
                Assert.Equal(1, SqlClientEventSourceProps.PooledConnections);
                Assert.Equal(0, SqlClientEventSourceProps.NonPooledConnections);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveConnectionPools);
                Assert.Equal(1, SqlClientEventSourceProps.ActiveConnections);
                Assert.Equal(0, SqlClientEventSourceProps.FreeConnections);
            }
        }

        [ConditionalFact(typeof(DataTestUtility), nameof(DataTestUtility.AreConnStringsSetup))]
        public void EventCounter_StasisCounters_Functional()
        {
            var stringBuilder = new SqlConnectionStringBuilder(DataTestUtility.TCPConnectionString) {Pooling = false};

            using (var conn = new SqlConnection(stringBuilder.ToString()))
            using (new TransactionScope())
            {
                conn.Open();
                conn.EnlistTransaction(System.Transactions.Transaction.Current);
                conn.Close();

                //when the connection gets closed, but the ambient transaction is still in prigress
                //the physical connection gets in stasis, until the transaction ends
                Assert.Equal(1, SqlClientEventSourceProps.StasisConnections);
            }

            //when the transaction finally ends, the physical connection is returned from stasis
            Assert.Equal(0, SqlClientEventSourceProps.StasisConnections);
        }

        private void ClearConnectionPools()
        {
            //ClearAllPoos kills all the existing pooled connection thus deactivating all the active pools
            var liveConnectionPools = SqlClientEventSourceProps.ActiveConnectionPools +
                                      SqlClientEventSourceProps.InactiveConnectionPools;
            ClearAllPools();
            Assert.InRange(SqlClientEventSourceProps.InactiveConnectionPools, 0, liveConnectionPools);
            Assert.Equal(0, SqlClientEventSourceProps.ActiveConnectionPools);

            //the 1st PruneConnectionPoolGroups call cleans the dangling inactive connection pools
            PruneConnectionPoolGroups();
            Assert.Equal(0, SqlClientEventSourceProps.InactiveConnectionPools);

            //the 2nd call deactivates the dangling connection pool groups
            var liveConnectionPoolGroups = SqlClientEventSourceProps.ActiveConnectionPoolGroups +
                                           SqlClientEventSourceProps.InactiveConnectionPoolGroups;
            PruneConnectionPoolGroups();
            Assert.InRange(SqlClientEventSourceProps.InactiveConnectionPoolGroups, 0, liveConnectionPoolGroups);
            Assert.Equal(0, SqlClientEventSourceProps.ActiveConnectionPoolGroups);

            //the 3rd call cleans the dangling connection pool groups
            PruneConnectionPoolGroups();
            Assert.Equal(0, SqlClientEventSourceProps.InactiveConnectionPoolGroups);
        }

        private static void ClearAllPools()
        {
            FieldInfo connectionFactoryField = GetConnectionFactoryField();
            MethodInfo clearAllPoolsMethod =
                connectionFactoryField.FieldType.GetMethod("ClearAllPools",
                    BindingFlags.Public | BindingFlags.Instance);
            Debug.Assert(clearAllPoolsMethod != null);
            clearAllPoolsMethod.Invoke(connectionFactoryField.GetValue(null), Array.Empty<object>());
        }

        private static void PruneConnectionPoolGroups()
        {
            FieldInfo connectionFactoryField = GetConnectionFactoryField();
            MethodInfo pruneConnectionPoolGroupsMethod =
                connectionFactoryField.FieldType.GetMethod("PruneConnectionPoolGroups",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Assert(pruneConnectionPoolGroupsMethod != null);
            pruneConnectionPoolGroupsMethod.Invoke(connectionFactoryField.GetValue(null), new[] {(object)null});
        }

        private static FieldInfo GetConnectionFactoryField()
        {
            FieldInfo connectionFactoryField =
                typeof(SqlConnection).GetField("s_connectionFactory", BindingFlags.Static | BindingFlags.NonPublic);
            Debug.Assert(connectionFactoryField != null);
            return connectionFactoryField;
        }
    }

    internal static class SqlClientEventSourceProps
    {
        private static readonly object _log;
        private static readonly FieldInfo _activeHardConnectionsCounter;
        private static readonly FieldInfo _activeSoftConnectionsCounter;
        private static readonly FieldInfo _nonPooledConnectionsCounter;
        private static readonly FieldInfo _pooledConnectionsCounter;
        private static readonly FieldInfo _activeConnectionPoolGroupsCounter;
        private static readonly FieldInfo _inactiveConnectionPoolGroupsCounter;
        private static readonly FieldInfo _activeConnectionPoolsCounter;
        private static readonly FieldInfo _inactiveConnectionPoolsCounter;
        private static readonly FieldInfo _activeConnectionsCounter;
        private static readonly FieldInfo _freeConnectionsCounter;
        private static readonly FieldInfo _stasisConnectionsCounter;

        static SqlClientEventSourceProps()
        {
            var sqlClientEventSourceType =
                Assembly.GetAssembly(typeof(SqlConnection))!.GetType("Microsoft.Data.SqlClient.SqlClientEventSource");
            Debug.Assert(sqlClientEventSourceType != null);
            var logField = sqlClientEventSourceType.GetField("Log", BindingFlags.Static | BindingFlags.NonPublic);
            Debug.Assert(logField != null);
            _log = logField.GetValue(null);

            var _bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            _activeHardConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_activeHardConnectionsCounter), _bindingFlags);
            Debug.Assert(_activeHardConnectionsCounter != null);
            _activeSoftConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_activeSoftConnectionsCounter), _bindingFlags);
            Debug.Assert(_activeSoftConnectionsCounter != null);
            _nonPooledConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_nonPooledConnectionsCounter), _bindingFlags);
            Debug.Assert(_nonPooledConnectionsCounter != null);
            _pooledConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_pooledConnectionsCounter), _bindingFlags);
            Debug.Assert(_pooledConnectionsCounter != null);
            _activeConnectionPoolGroupsCounter =
                sqlClientEventSourceType.GetField(nameof(_activeConnectionPoolGroupsCounter), _bindingFlags);
            Debug.Assert(_activeConnectionPoolGroupsCounter != null);
            _inactiveConnectionPoolGroupsCounter =
                sqlClientEventSourceType.GetField(nameof(_inactiveConnectionPoolGroupsCounter), _bindingFlags);
            Debug.Assert(_inactiveConnectionPoolGroupsCounter != null);
            _activeConnectionPoolsCounter =
                sqlClientEventSourceType.GetField(nameof(_activeConnectionPoolsCounter), _bindingFlags);
            Debug.Assert(_activeConnectionPoolsCounter != null);
            _inactiveConnectionPoolsCounter =
                sqlClientEventSourceType.GetField(nameof(_inactiveConnectionPoolsCounter), _bindingFlags);
            Debug.Assert(_inactiveConnectionPoolsCounter != null);
            _activeConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_activeConnectionsCounter), _bindingFlags);
            Debug.Assert(_activeConnectionsCounter != null);
            _freeConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_freeConnectionsCounter), _bindingFlags);
            Debug.Assert(_freeConnectionsCounter != null);
            _stasisConnectionsCounter =
                sqlClientEventSourceType.GetField(nameof(_stasisConnectionsCounter), _bindingFlags);
            Debug.Assert(_stasisConnectionsCounter != null);
        }

        public static long ActiveHardConnections => (long)_activeHardConnectionsCounter.GetValue(_log)!;

        public static long ActiveSoftConnections => (long)_activeSoftConnectionsCounter.GetValue(_log)!;

        public static long NonPooledConnections => (long)_nonPooledConnectionsCounter.GetValue(_log)!;

        public static long PooledConnections => (long)_pooledConnectionsCounter.GetValue(_log)!;

        public static long ActiveConnectionPoolGroups => (long)_activeConnectionPoolGroupsCounter.GetValue(_log)!;

        public static long InactiveConnectionPoolGroups => (long)_inactiveConnectionPoolGroupsCounter.GetValue(_log)!;

        public static long ActiveConnectionPools => (long)_activeConnectionPoolsCounter.GetValue(_log)!;

        public static long InactiveConnectionPools => (long)_inactiveConnectionPoolsCounter.GetValue(_log)!;

        public static long ActiveConnections => (long)_activeConnectionsCounter.GetValue(_log)!;

        public static long FreeConnections => (long)_freeConnectionsCounter.GetValue(_log)!;

        public static long StasisConnections => (long)_stasisConnectionsCounter.GetValue(_log)!;
    }
}
