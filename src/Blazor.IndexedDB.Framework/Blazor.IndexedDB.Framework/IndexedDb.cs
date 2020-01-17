using Blazor.IndexedDB.Framework.Attributes;
using Blazor.IndexedDB.Framework.Extensions;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TG.Blazor.IndexedDB;

namespace Blazor.IndexedDB.Framework
{
    public abstract class IndexedDb : IDisposable
    {
        private readonly Task init;

        private IndexedDBManager connector;

        private IEnumerable<PropertyInfo> cachedTables;

        private IEnumerable<PropertyInfo> getTables() {
            if(cachedTables is null){
                cachedTables = this.GetType().GetProperties()
                .Where(x => x.PropertyType.IsGenericType && x.PropertyType.GetGenericTypeDefinition() == typeof(IndexedSet<>));
            }
            return cachedTables;
        }

        public IndexedDb(IJSRuntime jSRuntime, string name, int version)
        {
            this.Version = version;
            this.Name = name;

            var dbStore = new DbStore()
            {
                DbName = this.Name,
                Version = this.Version,
            };

            this.Build(dbStore);

            this.connector = new IndexedDBManager(dbStore, jSRuntime);

            this.init = this.LoadData();

            this.connector.ActionCompleted += Connector_ActionCompleted;
        }

        private void Connector_ActionCompleted(object sender, IndexedDBNotificationArgs e)
        {
            //Debug.WriteLine(e.Message);
        }

        /// <summary>
        /// The database name
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The database version
        /// </summary>
        public int Version { get; }

        public async Task<bool> WaitForConnection()
        {
            await Task.WhenAll(this.init);

            return this.init.IsCompleted && !this.init.IsFaulted;
        }

        public async Task SaveChanges()
        {
            //Debug.WriteLine($"Saving changes...");

            foreach (var table in this.getTables())
            {
                var indexedSet = table.GetValue(this) as IndexedSet;
                
                // Find pk here to reduce required save time if more than one row has been deleted 
                PropertyInfo pkProperty = null;

                foreach (var row in indexedSet.GetChanged())
                {
                    switch (row.State)
                    {
                        case EntityState.Detached:
                        case EntityState.Unchanged:
                            continue;
                        case EntityState.Added:
                            await this.AddRow(table.Name, row.Instance);
                            break;
                        case EntityState.Deleted:
                            if (pkProperty == null)
                            {
                                pkProperty = this.GetPrimaryKey(row.Instance.GetType(), table.Name);
                            }

                            await this.DeleteRow(table.Name, row.Instance, pkProperty);
                            break;
                        case EntityState.Modified:
                            await this.ChangeRow(table.Name, row.Instance);
                            break;
                        default:
                            throw new NotSupportedException();
                    }
                }
            }
        }

        /// <summary>
        /// Disposal of any managed / unmanaged ressource
        /// </summary>
        public void Dispose()
        {
            this.init?.Dispose();
        }

        /// <summary>
        /// Builds the schema
        /// </summary>
        /// <param name="dbStore"></param>
        private void Build(DbStore dbStore)
        {
            // For all tables
            foreach (var schemaProperty in this.getTables())
            {
                // Create schema
                var schema = new StoreSchema()
                {
                    Name = schemaProperty.Name,
                    Indexes = new List<IndexSpec>(),
                };

                // Get generic parameter of list<T> (type T, only supports IndexedSet<T> ergo 1 parameter)
                var propertyType = schemaProperty.PropertyType.GetGenericArguments()[0];

                // Get all properties of the generic type T
                var properties = propertyType.GetProperties();

                foreach (var property in properties)
                {
                    // If any non supported object is used throw exception here
                    if (property.PropertyType.IsGenericType && !property.PropertyType.Namespace.StartsWith("System"))
                    {
                        throw new NotSupportedException(property.PropertyType.FullName);
                    }

                    // Get attributes from the entity property, ergo column
                    var attributes = property.CustomAttributes;

                    var id = false;
                    var unique = false;
                    var autoIncrement = false;
                    var foreignKey = false;

                    // Check for settings via attributes here (additonal attributes have to be checked here)
                    if (attributes.Any(x => x.AttributeType == typeof(KeyAttribute)))
                    {
                        id = true;
                    }
                    if (attributes.Any(x => x.AttributeType == typeof(UniqueAttribute)))
                    {
                        unique = true;
                    }
                    if (attributes.Any(x => x.AttributeType == typeof(AutoIncrementAttribute)))
                    {
                        autoIncrement = true;
                    }
                    if (attributes.Any(x => x.AttributeType == typeof(ForeignKeyAttribute)))
                    {
                        if (id)
                        {
                            throw new NotSupportedException("PK cannot be FK");
                        }

                        foreignKey = true;
                    }

                    var columnName = this.FirstToLower(property.Name);
                    // Define index
                    var index = new IndexSpec { Name = columnName, KeyPath = columnName, Auto = autoIncrement, Unique = unique };

                    // Register index
                    if (id)
                    {
                        // Throw invalid operation when index has already been defined
                        if (schema.PrimaryKey != null)
                        {
                            throw new InvalidOperationException("PK already defined");
                        }

                        index.Auto = true;
                        schema.PrimaryKey = index;
                    }
                    else if (!foreignKey)
                    {

                        schema.Indexes.Add(index);
                    }
                }

                // Create PK when not defined
                if (schema.PrimaryKey == null)
                {
                    var idPropertyName = "Id";
                    var idColumnName = this.FirstToLower(idPropertyName);

                    // Check for registered id property without declared key attribute
                    if (properties.Any(x => x.Name == idPropertyName))
                    {
                        var idProperty = schema.Indexes.Single(x => x.Name == idColumnName);

                        // Remove from schema
                        schema.Indexes.Remove(idProperty);

                        // And set as primary key
                        schema.PrimaryKey = idProperty;

                        // Set to auto, default setting
                        schema.PrimaryKey.Auto = true;
                    }
                    else
                    {
                        throw new NotSupportedException("Missing id property");
                        // Not supported because no implementation for changed check when deleted (missing id to resolve object in store)
                        //schema.PrimaryKey = new IndexSpec { Name = idColumnName, KeyPath = idColumnName, Auto = true };
                    }
                }

                // Add schema to registered schemas
                dbStore.Stores.Add(schema);
            }
            //Debug.WriteLine($"{nameof(IndexedDb)} - Schema has been built");
        }

        /// <summary>
        /// First char to lower case
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        private string FirstToLower(string input)
        {
            if (input != string.Empty && char.IsUpper(input[0]))
            {
                input = char.ToLower(input[0]) + input.Substring(1);
            }

            return input;
        }


        private Dictionary<Type, IEnumerable<PropertyInfo>> rowTypes = new Dictionary<Type, IEnumerable<PropertyInfo>>();
        /// <summary>
        /// Gets the pk from a table by any row type hold within the table
        /// </summary>
        /// <param name="rowType"></param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        private PropertyInfo GetPrimaryKey(Type rowType, string tableName)
        {

            var storePk = this.connector.Stores.Single(x => x.Name == tableName).PrimaryKey.KeyPath;

            IEnumerable<PropertyInfo> properties;
            if(rowTypes.ContainsKey(rowType)){
                properties = rowTypes[rowType];
            }else{
                properties = rowType.GetProperties();
                rowTypes.Add(rowType, properties);
            }


            return properties.Single(x => this.FirstToLower(x.Name) == storePk);
        }

        private async Task LoadData()
        {
            foreach (var table in this.getTables())
            {
                // Get generic parameter of list<T> (type T, only supports IndexedSet<T> ergo 1 parameter)
                var propertyType = table.PropertyType.GetGenericArguments()[0];

                // Get generic records of table
                var records = await this.GetRows(propertyType, table.Name);

                var pkProperty = this.GetPrimaryKey(propertyType, table.Name);

                table.SetValue(this, Activator.CreateInstance(table.PropertyType, records, pkProperty));

            }
        }

        private async Task<object> GetRows(Type propertyType, string storeName)
        {
            MethodInfo method = this.connector.GetType().GetMethod(nameof(this.connector.GetRecords));
            MethodInfo generic = method.MakeGenericMethod(propertyType);
            var records = await generic.InvokeAsyncWithResult(this.connector, new object[] { storeName });   
            return records;
        }

        /// <summary>
        /// Removes a row from the store
        /// </summary>
        /// <param name="storeName"></param>
        /// <param name="data"></param>
        /// <param name="pkProperty"></param>
        /// <returns></returns>
        private async Task DeleteRow(string storeName, object data, PropertyInfo pkProperty)
        {
            var pkValue = pkProperty.GetValue(data);

            await this.connector.DeleteRecord<object>(storeName, pkValue);
        }

        /// <summary>
        /// Adds a row to the store
        /// </summary>
        /// <param name="storeName"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task AddRow(string storeName, object data)
        {
            var storeRecord = this.ConvertToObjectRecord(storeName, data);

            await this.connector.AddRecord<object>(storeRecord);
        }

        /// <summary>
        /// Changes the row data in the store
        /// </summary>
        /// <param name="storeName"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task ChangeRow(string storeName, object data)
        {
            var storeRecord = this.ConvertToObjectRecord(storeName, data);

            await this.connector.UpdateRecord<object>(storeRecord);
        }

        private Dictionary<Type, IEnumerable<PropertyInfo>> dataProperties = new Dictionary<Type, IEnumerable<PropertyInfo>>();

        private IEnumerable<PropertyInfo> GetDataProperties(object data){
            if(dataProperties.ContainsKey(data.GetType())){
                return dataProperties[data.GetType()];
            }
            var properties = data.GetType().GetProperties().Where(x => x.CustomAttributes.All(y => y.AttributeType != typeof(ForeignKeyAttribute)));
            dataProperties.Add(data.GetType(), properties);
            return properties;
        }

        // Only required to save to database, resolve will be = to object properties
        // Because -> Obj Person {"FirstName":"A","LastName":"B"} = Dictionary<string, object> {"FirstName":"A","LastName":"B"}
        // Usage dictionary instead of type for the possibility of ignoring properties, eg ForeignKeyAttribute properties
        private StoreRecord<object> ConvertToObjectRecord(string storeName, object data)
        {
            var properties = GetDataProperties(data);

            var keyValueData = new Dictionary<string, object>();

            foreach (var property in properties)
            {
                var propertyNameInStore = this.FirstToLower(property.Name);

                keyValueData.Add(propertyNameInStore, property.GetValue(data));
            }

            var storeRecord = new StoreRecord<object>()
            {
                Data = keyValueData,
                Storename = storeName
            };

            return storeRecord;
        }
    }
}
