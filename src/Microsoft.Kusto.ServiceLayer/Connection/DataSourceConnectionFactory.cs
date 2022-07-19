﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System.Data.Common;
using Microsoft.Kusto.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.ReliableConnection;

namespace Microsoft.Kusto.ServiceLayer.Connection
{
    /// <summary>
    /// Factory class to create SqlClientConnections
    /// The purpose of the factory is to make it easier to mock out the database
    /// in 'offline' unit test scenarios.
    /// </summary>
    public class DataSourceConnectionFactory : IDataSourceConnectionFactory
    {
        /// <summary>
        /// Creates a new SqlConnection object
        /// </summary>
        public ReliableDataSourceConnection CreateDataSourceConnection(string connectionString, string azureAccountToken)
        {
            RetryPolicy connectionRetryPolicy = RetryPolicyFactory.CreateDefaultConnectionRetryPolicy();
            RetryPolicy commandRetryPolicy = RetryPolicyFactory.CreateDefaultConnectionRetryPolicy();
            return new ReliableDataSourceConnection(connectionString, connectionRetryPolicy, commandRetryPolicy, azureAccountToken);
        }
    }
}
