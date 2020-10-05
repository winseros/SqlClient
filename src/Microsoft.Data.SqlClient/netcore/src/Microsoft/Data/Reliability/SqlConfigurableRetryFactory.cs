﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Data.SqlClient
{
    /// Define the SQL command type by filtering purpose.
    [Flags]
    public enum FilterSqlStatements
    {
        /// Don't filter any SQL commands
        None = 0,
        /// Filter INSERT or INSERT INTO
        Insert = 1,
        /// Filter UPDATE
        Update = 2,
        /// Filter DELETE
        Delete = 1 << 2,
        /// Filter EXECUTE or EXEC
        Execute = 1 << 3,
        /// Filter ALTER
        Alter = 1 << 4,
        /// Filter CREATE
        Create = 1 << 5,
        /// Filter DROP
        Drop = 1 << 6,
        /// Filter TRUNCATE
        Truncate = 1 << 7,
        /// Filter SELECT
        Select = 1 << 8,
        /// Filter data manipulation commands consist of INSERT, INSERT INTO, UPDATE, and DELETE
        DML = Insert | Update | Delete | Truncate,
        /// Filter data definition commands consist of ALTER, CREATE, and DROP
        DDL = Alter | Create | Drop,
        /// Filter any SQL command types
        All = DML | DDL | Execute | Select
    }

    /// Provide different retry strategies. 
    public sealed class SqlConfigurableRetryFactory
    {
        /// Default known transient error numbers.
        private static readonly HashSet<int> s_defaultTransientErrors
            = new HashSet<int>
                {
                    1204,  // The instance of the SQL Server Database Engine cannot obtain a LOCK resource at this time. Rerun your statement when there are fewer active users. Ask the database administrator to check the lock and memory configuration for this instance, or to check for long-running transactions.
                    1205,  // Transaction (Process ID) was deadlocked on resources with another process and has been chosen as the deadlock victim. Rerun the transaction
                    1222,  // Lock request time out period exceeded.
                    49918,  // Cannot process request. Not enough resources to process request.
                    49919,  // Cannot process create or update request. Too many create or update operations in progress for subscription "%ld".
                    49920,  // Cannot process request. Too many operations in progress for subscription "%ld".
                    4060,  // Cannot open database "%.*ls" requested by the login. The login failed.
                    4221,  // Login to read-secondary failed due to long wait on 'HADR_DATABASE_WAIT_FOR_TRANSITION_TO_VERSIONING'. The replica is not available for login because row versions are missing for transactions that were in-flight when the replica was recycled. The issue can be resolved by rolling back or committing the active transactions on the primary replica. Occurrences of this condition can be minimized by avoiding long write transactions on the primary.

                    40143,  // The service has encountered an error processing your request. Please try again.
                    40613,  // Database '%.*ls' on server '%.*ls' is not currently available. Please retry the connection later. If the problem persists, contact customer support, and provide them the session tracing ID of '%.*ls'.
                    40501,  // The service is currently busy. Retry the request after 10 seconds. Incident ID: %ls. Code: %d.
                    40540,  // The service has encountered an error processing your request. Please try again.
                    40197,  // The service has encountered an error processing your request. Please try again. Error code %d.
                    10929,  // Resource ID: %d. The %s minimum guarantee is %d, maximum limit is %d and the current usage for the database is %d. However, the server is currently too busy to support requests greater than %d for this database. For more information, see http://go.microsoft.com/fwlink/?LinkId=267637. Otherwise, please try again later.
                    10928,  // Resource ID: %d. The %s limit for the database is %d and has been reached. For more information, see http://go.microsoft.com/fwlink/?LinkId=267637.
                    10060,  // An error has occurred while establishing a connection to the server. When connecting to SQL Server, this failure may be caused by the fact that under the default settings SQL Server does not allow remote connections. (provider: TCP Provider, error: 0 - A connection attempt failed because the connected party did not properly respond after a period of time, or established connection failed because connected host has failed to respond.) (Microsoft SQL Server, Error: 10060)
                    10054,  // The data value for one or more columns overflowed the type used by the provider.
                    10053,  // Could not convert the data value due to reasons other than sign mismatch or overflow.
                    233,    // A connection was successfully established with the server, but then an error occurred during the login process. (provider: Shared Memory Provider, error: 0 - No process is on the other end of the pipe.) (Microsoft SQL Server, Error: 233)
                    64,
                    20,
                    0,
                    0
                };

        /// Provide an exponential retry strategy.
        public static SqlRetryLogicBaseProvider CreateExponentialRetryProvider(int numberOfTries,
                                                TimeSpan deltaTimeBackoff,
                                                TimeSpan maxTimeInterval,
                                                FilterSqlStatements unauthorizedSqlStatements = FilterSqlStatements.DML,
                                                IEnumerable<int> transientErrors = null,
                                                TimeSpan minTimeInterval = default)
        {
            var retryLogic = new SqlRetryLogic(numberOfTries,
                                        new SqlExponentialIntervalEnumerator(deltaTimeBackoff, maxTimeInterval, minTimeInterval),
                                        (e) => TransientErrorsCondition(e, transientErrors ?? s_defaultTransientErrors),
                                        RetryPreConditon(unauthorizedSqlStatements));

            return new SqlRetryLogicProvider(retryLogic);
        }

        /// Provide an incrimental retry strategy.
        public static SqlRetryLogicBaseProvider CreateIncrimentalRetryProvider(int numberOfTries,
                                                TimeSpan timeInterval,
                                                TimeSpan maxTimeInterval,
                                                FilterSqlStatements unauthorizedSqlStatements = FilterSqlStatements.DML,
                                                IEnumerable<int> transientErrors = null,
                                                TimeSpan minTimeInterval = default)
        {
            var retryLogic = new SqlRetryLogic(numberOfTries,
                                        new SqlIncrementalIntervalEnumerator(timeInterval, maxTimeInterval, minTimeInterval),
                                        (e) => TransientErrorsCondition(e, transientErrors ?? s_defaultTransientErrors),
                                        RetryPreConditon(unauthorizedSqlStatements));

            return new SqlRetryLogicProvider(retryLogic);
        }

        /// Provide a fixed linear retry strategy.
        public static SqlRetryLogicBaseProvider CreateFixedRetryProvider(int numberOfTries,
                                                TimeSpan timeInterval,
                                                FilterSqlStatements unauthorizedSqlStatements = FilterSqlStatements.DML,
                                                IEnumerable<int> transientErrors = null)
        {
            var retryLogic = new SqlRetryLogic(numberOfTries,
                                        new SqlFixedIntervalEnumerator(timeInterval),
                                        (e) => TransientErrorsCondition(e, transientErrors ?? s_defaultTransientErrors),
                                        RetryPreConditon(unauthorizedSqlStatements));

            return new SqlRetryLogicProvider(retryLogic);
        }

        /// Provide a none retry strategy.
        public static SqlRetryLogicBaseProvider CreateNoneRetryProvider()
        {
            var retryLogic = new SqlRetryLogic(new SqlNoneIntervalEnumerator());

            return new SqlRetryLogicProvider(retryLogic);
        }

        #region private
        /// Return true if the exception is a transient fault or a Timeout exception.
        private static bool TransientErrorsCondition(Exception e, IEnumerable<int> retriableConditions)
        {
            bool result = false;

            if (e is SqlException ex)
            {
                foreach (SqlError item in ex.Errors)
                {
                    if (retriableConditions.Count(x => x == item.Number) > 0)
                    {
                        result = true;
                        break;
                    }
                }
            }
            else if (e is TimeoutException)
            {
                result = true;
            }
            return result;
        }

        /// Generate a predicate function to skip unauthorized SQL commands.
        private static Predicate<string> RetryPreConditon(FilterSqlStatements unauthorizedSqlStatements)
        {
            var pattern = GetRegexPattern(unauthorizedSqlStatements);
            return (commandText) => string.IsNullOrEmpty(pattern)
                                    || !Regex.IsMatch(commandText, pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        /// Provide a regex pattern regarding to the SQL statement.
        private static string GetRegexPattern(FilterSqlStatements sqlStatements)
        {
            if (sqlStatements == FilterSqlStatements.None)
            {
                return string.Empty;
            }

            var pattern = new StringBuilder();

            if (sqlStatements.HasFlag(FilterSqlStatements.Insert))
            {
                pattern.Append(@"INSERT( +INTO){0,1}|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Update))
            {
                pattern.Append(@"UPDATE|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Delete))
            {
                pattern.Append(@"DELETE|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Execute))
            {
                pattern.Append(@"EXEC(UTE){0,1}|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Alter))
            {
                pattern.Append(@"ALTER|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Create))
            {
                pattern.Append(@"CREATE|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Drop))
            {
                pattern.Append(@"DROP|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Truncate))
            {
                pattern.Append(@"TRUNCATE|");
            }
            if (sqlStatements.HasFlag(FilterSqlStatements.Select))
            {
                pattern.Append(@"SELECT|");
            }
            if (pattern.Length > 0)
            {
                pattern.Remove(pattern.Length - 1, 1);
            }
            return string.Format(@"\b({0})\b", pattern.ToString());
        }
        #endregion
    }
}
