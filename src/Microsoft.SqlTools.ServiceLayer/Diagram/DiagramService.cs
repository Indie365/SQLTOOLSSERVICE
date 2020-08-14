//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Threading.Tasks;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Hosting;
using Microsoft.SqlTools.ServiceLayer.Diagram.Contracts;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Microsoft.SqlTools.ServiceLayer.Metadata.Contracts;
using Microsoft.SqlTools.ServiceLayer.Utility;

namespace Microsoft.SqlTools.ServiceLayer.Diagram
{
    /// <summary>
    /// Main class for Diagram Service functionality
    /// </summary>
    public sealed class DiagramService
    {
        private static readonly Lazy<DiagramService> LazyInstance = new Lazy<DiagramService>(() => new DiagramService());

        public static DiagramService Instance => LazyInstance.Value;

        internal static Task DiagramModelTask { get; set; }

        private static ConnectionService connectionService = null;

        /// <summary>
        /// Internal for testing purposes only
        /// </summary>
        internal static ConnectionService ConnectionServiceInstance
        {
            get
            {
                if (connectionService == null)
                {
                    connectionService = ConnectionService.Instance;
                }
                return connectionService;
            }

            set
            {
                connectionService = value;
            }
        }

        /// <summary>
        /// Initializes the Metadata Service instance
        /// </summary>
        /// <param name="serviceHost"></param>
        /// <param name="context"></param>
        public void InitializeService(ServiceHost serviceHost)
        {
            serviceHost.SetRequestHandler(DiagramModelRequest.Type, HandleDiagramModelRequest);
        }

        /// <summary>
        /// Handle a metadata query request
        /// </summary>        
        internal static async Task HandleDiagramModelRequest(
            DiagramRequestParams metadataParams,
            RequestContext<DiagramRequestResult> requestContext)
        {
            try
            {
                Func<Task> requestHandler = async () =>
                {
                    ConnectionInfo connInfo;
                    DiagramService.ConnectionServiceInstance.TryFindConnection(
                        metadataParams.OwnerUri,
                        out connInfo);



                    string databaseName = metadataParams.Database;
                    string schemaName = metadataParams.Database;
                    string tableName = metadataParams.Database;
                    var metadata = new DiagramMetadata();
                    if (connInfo != null)
                    {
                        using (SqlConnection sqlConn = ConnectionService.OpenSqlConnection(connInfo, "DiagramModel"))
                        {
                            switch (metadataParams.DiagramView)
                            {
                                case (DiagramObject.Database):
                                    ReadDbMetadata(sqlConn, databaseName, metadata);
                                    break;
                                case (DiagramObject.Schema):
                                    ReadSchemaMetadata(sqlConn, schemaName, metadata);
                                    break;
                                case (DiagramObject.Table):
                                    ReadTableMetadata(sqlConn, databaseName, metadata);
                                    break;
                                default:
                                    ReadDbMetadata(sqlConn, databaseName, metadata);
                                    await requestContext.SendResult(new DiagramRequestResult
                                    {
                                        Metadata = metadata
                                    });
                                    break;
                            }
                        }
                    }

                };

                Task task = Task.Run(async () => await requestHandler()).ContinueWithOnFaulted(async t =>
                {
                    await requestContext.SendError(t.Exception.ToString());
                });

                DiagramModelTask = task;
            }
            catch (Exception ex)
            {
                await requestContext.SendError(ex.ToString());
            }
        }

        internal static bool IsSystemDatabase(string database)
        {
            // compare against master for now
            return string.Compare("master", database, StringComparison.OrdinalIgnoreCase) == 0;
        }

        internal static void ReadDbMetadata(SqlConnection sqlConn, string databaseName,
        DiagramMetadata metadata)
        {
            metadata.Name = databaseName;
            string properties_sql =
                $@"SELECT
                        b.database_id,
                        SUM(a.size * 8/1024) 'Size (MB)',
                        b.create_date,
                        b.user_access_desc
                    FROM sys.master_files a 
                    INNER JOIN sys.databases b
                    ON a.database_id = b.database_id
                    WHERE DB_NAME(a.database_id) = '{databaseName}' AND b.name = '{databaseName}'
                    GROUP BY b.database_id, b.create_date, b.user_access_desc
                    ";

            using (SqlCommand propertiesSqlCommand = new SqlCommand(properties_sql, sqlConn))
            {
                using (var reader = propertiesSqlCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var databaseId = reader[0] as string;
                        var size = reader[1] as string;
                        var createDate = reader[2] as string;
                        var userAccess = reader[3] as string;
                        metadata.Properties.Add("databaseId", databaseId);
                        metadata.Properties.Add("size", size);
                        metadata.Properties.Add("createDate", createDate);
                        metadata.Properties.Add("userAccess", userAccess);
                    }


                }
            }

            string schema_sql =
                @"SELECT SCHEMA_NAME, SCHEMA_OWNER, SCHEMA_ID(SCHEMA_NAME) AS SCHEMA_ID
                FROM INFORMATION_SCHEMA.SCHEMATA
                ORDER BY SCHEMA_ID ";

            using (SqlCommand schemaSqlCommand = new SqlCommand(schema_sql, sqlConn))
            {
                using (var reader = schemaSqlCommand.ExecuteReader())
                {
                    var dbSchemasGrid = new GridData();
                    var dbSchemasRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var schemaName = reader[0] as string;
                        var schemaOwner = reader[1] as string;
                        var schemaID = reader[2] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("schemaName", schemaName);
                        row.Add("schemaOwner", schemaName);
                        row.Add("schemaId", schemaName);
                        dbSchemasRows.Add(row);
                    }
                    dbSchemasGrid.rows = dbSchemasRows.ToArray();
                    metadata.Grids.Add("dbSchemasGrid", dbSchemasGrid);
                }
            }

            string tableSql =
                @"SELECT
                    t.NAME AS TableName,
                    s.Name AS SchemaName,
                    p.rows AS 'RowCount',
                    SUM(a.used_pages) * 8 AS UsedSpaceKB
                FROM
                    sys.tables t
                INNER JOIN
                    sys.indexes i ON t.OBJECT_ID = i.object_id
                INNER JOIN
                    sys.partitions p ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
                INNER JOIN
                    sys.allocation_units a ON p.partition_id = a.container_id
                LEFT OUTER JOIN
                    sys.schemas s ON t.schema_id = s.schema_id
                GROUP BY
                    t.Name, s.Name, p.Rows
                ORDER BY
                    t.Name";

            using (SqlCommand tableSqlCommand = new SqlCommand(tableSql, sqlConn))
            {
                using (var reader = tableSqlCommand.ExecuteReader())
                {
                    var dbTablesGrid = new GridData();
                    var dbTablesRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var tableName = reader[0] as string;
                        var schemaName = reader[1] as string;
                        var rowCount = reader[2] as string;
                        var size = reader[3] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("tableName", tableName);
                        row.Add("schemaName", schemaName);
                        row.Add("rowCount", rowCount);
                        row.Add("size", size);
                        dbTablesRows.Add(row);
                    }
                    dbTablesGrid.rows = dbTablesRows.ToArray();
                    metadata.Grids.Add("dbTablesGrid", dbTablesGrid);
                }
            }
        }


        internal static void ReadSchemaMetadata(SqlConnection sqlConn, string schemaName,
        DiagramMetadata metadata)
        {
            metadata.Name = schemaName;
            // Composite formatting: Console.WriteLine("Hello, {0}! Today is {1}, it's {2:HH:mm} now.", name, date.DayOfWeek, date); /
            string tables_sql =
                 $@"SELECT
                        t.NAME AS TableName,
                        p.rows AS 'RowCount',
                        SUM(a.used_pages) * 8 AS UsedSpaceKB
                    FROM
                        sys.tables t
                    INNER JOIN
                        sys.indexes i ON t.OBJECT_ID = i.object_id
                    INNER JOIN
                        sys.partitions p ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
                    INNER JOIN
                        sys.allocation_units a ON p.partition_id = a.container_id
                    LEFT OUTER JOIN
                        sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = '{schemaName}'
                    GROUP BY
                        t.Name, s.Name, p.Rows
                    ORDER BY
                        t.Name
                    ";

            using (SqlCommand tablesSqlCommand = new SqlCommand(tables_sql, sqlConn))
            {
                using (var reader = tablesSqlCommand.ExecuteReader())
                {
                    var schemaTablesGrid = new GridData();
                    var schemaTablesRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var tableName = reader[0] as string;
                        var rowCount = reader[1] as string;
                        var size = reader[2] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("tableName", tableName);
                        row.Add("rowCount", rowCount);
                        row.Add("size", size);
                        schemaTablesRows.Add(row);
                    }
                    schemaTablesGrid.rows = schemaTablesRows.ToArray();
                    metadata.Grids.Add("schemaTablesGrid", schemaTablesGrid);

                }
            }
        }

        internal static void ReadTableMetadata(SqlConnection sqlConn, string tableName,
        DiagramMetadata metadata)
        {
            metadata.Name = tableName;
            string keys_sql =
                $@"SELECT 
                        TC.CONSTRAINT_TYPE,
                        column_name as PRIMARYKEYCOLUMN
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS TC 

                    INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS KU
                        ON (TC.CONSTRAINT_TYPE = 'PRIMARY KEY' OR TC.CONSTRAINT_TYPE = 'FOREIGN KEY')
                        AND TC.CONSTRAINT_NAME = KU.CONSTRAINT_NAME 
                        AND KU.table_name='{tableName}'
                    ORDER BY 
                        KU.TABLE_NAME
                        ,KU.ORDINAL_POSITION
                    ;
                    ";

            using (SqlCommand keysSqlCommand = new SqlCommand(keys_sql, sqlConn))
            {
                using (var reader = keysSqlCommand.ExecuteReader())
                {
                    var tableKeysGrid = new GridData();
                    var tableKeysRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var keyType = reader[0] as string;
                        var keyName = reader[1] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("keyName", tableName);
                        row.Add("keyType", keyType);
                        tableKeysRows.Add(row);
                    }
                    tableKeysGrid.rows = tableKeysRows.ToArray();
                    metadata.Grids.Add("tableKeysGrid", tableKeysGrid);
                }

            }

            string columns_sql =
                $@"
                SELECT COLUMN_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE INFORMATION_SCHEMA.COLUMNS.TABLE_NAME = '{tableName}'";

            using (SqlCommand columnsSqlCommand = new SqlCommand(columns_sql, sqlConn))
            {
                using (var reader = columnsSqlCommand.ExecuteReader())
                {
                    var tableColumnsGrid = new GridData();
                    var tableColumnsRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var columnName = reader[0] as string;
                        var columnType = reader[1] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("columnName", columnName);
                        row.Add("columnType", columnType);
                        tableColumnsRows.Add(row);
                    }
                    tableColumnsGrid.rows = tableColumnsRows.ToArray();
                    metadata.Grids.Add("tableColumnsGrid", tableColumnsGrid);
                }
            }

            string relationships_sql =
                $@"SELECT
                    cu.TABLE_NAME AS ReferencingTable,
                    ku.TABLE_NAME AS ReferencedTable,
                    c.CONSTRAINT_NAME
                FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS c
                INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE cu
                ON cu.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                ON ku.CONSTRAINT_NAME = c.UNIQUE_CONSTRAINT_NAME
                WHERE ku.TABLE_NAME = '{tableName}' OR cu.TABLE_NAME = '{tableName}'
";

            using (SqlCommand relationshipsSqlCommand = new SqlCommand(relationships_sql, sqlConn))
            {
                using (var reader = relationshipsSqlCommand.ExecuteReader())
                {
                    var tableRelationshipsGrid = new GridData();
                    var tableRelationshipsRows = new List<Dictionary<string, string>>();
                    while (reader.Read())
                    {
                        var referencingTable = reader[0] as string;
                        var referencedTable = reader[1] as string;
                        var constraint = reader[2] as string;
                        var row = new Dictionary<string, string>();
                        row.Add("referencingTable ", referencingTable);
                        row.Add("referencedTable", referencedTable);
                        row.Add("constraint", constraint);
                        tableRelationshipsRows.Add(row);
                    }
                    tableRelationshipsGrid.rows = tableRelationshipsRows.ToArray();
                    metadata.Grids.Add("tableRelationshipsGrid", tableRelationshipsGrid);
                }
            }


        }

















    }
}

