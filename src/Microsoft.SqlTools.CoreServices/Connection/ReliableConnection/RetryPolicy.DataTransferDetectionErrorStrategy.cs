//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using Microsoft.Data.SqlClient;
using System.Diagnostics;
using Microsoft.SqlTools.Hosting.Utility;

namespace Microsoft.SqlTools.CoreServices.Connection.ReliableConnection
{
    internal abstract partial class RetryPolicy
    {
        /// <summary>
        /// Provides the error detection logic for temporary faults that are commonly found during data transfer.
        /// </summary>
        internal class DataTransferErrorDetectionStrategy : ErrorDetectionStrategyBase
        {
            private static readonly DataTransferErrorDetectionStrategy instance = new DataTransferErrorDetectionStrategy();

            public static DataTransferErrorDetectionStrategy Instance
            {
                get { return instance; }
            }
            
            protected override bool CanRetrySqlException(SqlException sqlException)
            {
                // Enumerate through all errors found in the exception.
                foreach (SqlError err in sqlException.Errors)
                {
                    RetryPolicyUtils.AppendThrottlingDataIfIsThrottlingError(sqlException, err);
                    if (RetryPolicyUtils.IsNonRetryableDataTransferError(err.Number))
                    {
                        Logger.Write(TraceEventType.Error, string.Format(Resources.ExceptionCannotBeRetried, err.Number, err.Message));
                        return false;
                    }
                }

                // Default is to treat all SqlException as retriable.
                return true;
            }
        }
    }
}
