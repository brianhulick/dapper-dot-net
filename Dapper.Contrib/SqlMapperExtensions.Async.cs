#if ASYNC
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Dapper;

#if COREFX
using IDbTransaction = System.Data.Common.DbTransaction;
using IDbConnection = System.Data.Common.DbConnection;
using DataException = System.InvalidOperationException;
#else
using System.Data;
#endif

namespace Dapper.Contrib.Extensions
{
    public static partial class SqlMapperExtensions
    {
        public static async Task<IEnumerable<TResult>> QueryByTCriteriaAsync<TResult, TCriteria>(this IDbConnection connection, TCriteria criteria, IDbTransaction transaction = null, int? commandTimeout = null)
            where TResult : class
            where TCriteria : class
        {
            var type = typeof(TResult);
            var criteriaType = typeof(TCriteria);
            var criteriaProps = TypePropertiesCache(criteriaType);
            var tblName = GetTableName(criteriaType);

            List<string> clauses = new List<string>();
            clauses.Add("SELECT *");
            clauses.Add("FROM " + tblName);

            List<string> where = new List<string>();
            List<string> orderBy = new List<string>();
            foreach (var prop in criteriaProps)
            {
                var critAttr = (CriteriaAttribute)prop.GetCustomAttributes(true).FirstOrDefault(a => a is CriteriaAttribute);
                var dbField = (critAttr != null && !string.IsNullOrEmpty(critAttr.DbField)) ? critAttr.DbField : prop.Name;

                if (critAttr != null && !string.IsNullOrEmpty(critAttr.OrderBy) && (critAttr.OrderBy.ToUpper() == "ASC" || critAttr.OrderBy.ToUpper() == "DESC"))
                    orderBy.Add(dbField + " " + critAttr.OrderBy);

                if (prop.GetValue(criteria, null) != null)
                {
                    bool isCollection = prop.PropertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType);
                    var op = (critAttr != null && !string.IsNullOrEmpty(critAttr.EqualityOperator)) ? critAttr.EqualityOperator : (isCollection) ? "IN" : "=";
                    where.Add(string.Format("{0} {1} @{2}", dbField, op, prop.Name));
                }
            }

            if (where.Count > 0)
                clauses.Add("WHERE " + string.Join(" AND ", where));

            if (orderBy.Count > 0)
                clauses.Add("ORDER BY " + string.Join(", ", orderBy));

            var sql = string.Join(" ", clauses);

            return await connection.QueryAsync<TResult>(sql, criteria, transaction, commandTimeout: commandTimeout);
        }

        public static async Task<TResult> GetByTIdentityAsync<TResult, TIdentity>(this IDbConnection connection, TIdentity id, IDbTransaction transaction = null, int? commandTimeout = null) where TResult : class
        {
            var type = typeof(TResult);
            var idType = typeof(TIdentity);
            var keys = TypePropertiesCache(idType);

            string sql;
            if (!GetByTIdentityQueries.TryGetValue(type.TypeHandle, out sql))
            {
                var name = GetTableName(type);

                sql = $"select * from {name}";
                for (var keyCount = 0; keyCount < keys.Count; keyCount++)
                {
                    sql += ((keyCount == 0) ? $" WHERE " : $" AND ") + $"{keys[keyCount].Name} = @{keys[keyCount].Name}";
                }
                GetByTIdentityQueries[type.TypeHandle] = sql;
            }

            if (!type.IsInterface())
                return (await connection.QueryAsync<TResult>(sql, id, transaction, commandTimeout).ConfigureAwait(false)).FirstOrDefault();

            var res = (await connection.QueryAsync<dynamic>(sql, id).ConfigureAwait(false)).FirstOrDefault() as IDictionary<string, object>;

            if (res == null)
                return null;

            var obj = ProxyGenerator.GetInterfaceProxy<TResult>();

            foreach (var property in TypePropertiesCache(type))
            {
                var val = res[property.Name];
                property.SetValue(obj, Convert.ChangeType(val, property.PropertyType), null);
            }

            ((IProxy)obj).IsDirty = false;   //reset change tracking and return

            return obj;
        }

        /// <summary>
        /// Returns a single entity by a single id from table "Ts" asynchronously using .NET 4.5 Task. T must be of interface type. 
        /// Id must be marked with [Key] attribute.
        /// Created entity is tracked/intercepted for changes and used by the Update() extension. 
        /// </summary>
        /// <typeparam name="T">Interface type to create and populate</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="id">Id of the entity to get, must be marked with [Key] attribute</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Entity of T</returns>
        public static async Task<T> GetAsync<T>(this IDbConnection connection, dynamic id, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var type = typeof(T);
            string sql;
            if (!GetQueries.TryGetValue(type.TypeHandle, out sql))
            {
                var key = GetSingleKey<T>(nameof(GetAsync));
                var name = GetTableName(type);

                sql = $"SELECT * FROM {name} WHERE {key.Name} = @id";
                GetQueries[type.TypeHandle] = sql;
            }

            var dynParms = new DynamicParameters();
            dynParms.Add("@id", id);

            if (!type.IsInterface())
                return (await connection.QueryAsync<T>(sql, dynParms, transaction, commandTimeout).ConfigureAwait(false)).FirstOrDefault();

            var res = (await connection.QueryAsync<dynamic>(sql, dynParms).ConfigureAwait(false)).FirstOrDefault() as IDictionary<string, object>;

            if (res == null)
                return null;

            var obj = ProxyGenerator.GetInterfaceProxy<T>();

            foreach (var property in TypePropertiesCache(type))
            {
                var val = res[property.Name];
                property.SetValue(obj, Convert.ChangeType(val, property.PropertyType), null);
            }

            ((IProxy)obj).IsDirty = false;   //reset change tracking and return

            return obj;
        }

        /// <summary>
        /// Returns a list of entites from table "Ts".  
        /// Id of T must be marked with [Key] attribute.
        /// Entities created from interfaces are tracked/intercepted for changes and used by the Update() extension
        /// for optimal performance. 
        /// </summary>
        /// <typeparam name="T">Interface or type to create and populate</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Entity of T</returns>
        public static Task<IEnumerable<T>> GetAllAsync<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var type = typeof(T);
            var cacheType = typeof(List<T>);

            string sql;
            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out sql))
            {
                GetSingleKey<T>(nameof(GetAll));
                var name = GetTableName(type);

                sql = "SELECT * FROM " + name;
                GetQueries[cacheType.TypeHandle] = sql;
            }

            if (!type.IsInterface())
            {
                return connection.QueryAsync<T>(sql, null, transaction, commandTimeout);
            }
            return GetAllAsyncImpl<T>(connection, transaction, commandTimeout, sql, type);
        }
        private static async Task<IEnumerable<T>> GetAllAsyncImpl<T>(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string sql, Type type) where T : class
        {
            var result = await connection.QueryAsync(sql);
            var list = new List<T>();
            foreach (IDictionary<string, object> res in result)
            {
                var obj = ProxyGenerator.GetInterfaceProxy<T>();
                foreach (var property in TypePropertiesCache(type))
                {
                    var val = res[property.Name];
                    property.SetValue(obj, Convert.ChangeType(val, property.PropertyType), null);
                }
                ((IProxy)obj).IsDirty = false;   //reset change tracking and return
                list.Add(obj);
            }
            return list;
        }


        /// <summary>
        /// Inserts an entity into table "Ts" asynchronously using .NET 4.5 Task and returns identity id.
        /// </summary>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToInsert">Entity to insert</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <param name="sqlAdapter">The specific ISqlAdapter to use, auto-detected based on connection if null</param>
        /// <returns>Identity of inserted entity</returns>
        public static Task<int> InsertAsync<T>(this IDbConnection connection, T entityToInsert, IDbTransaction transaction = null,
            int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class
        {
            var type = typeof(T);
            if (sqlAdapter == null)
                sqlAdapter = GetFormatter(connection);

            var isList = false;
            if (type.IsArray || type.IsGenericType())
            {
                isList = true;
                type = type.GetGenericArguments()[0];
            }

            var name = GetTableName(type);
            var sbColumnList = new StringBuilder(null);
            var allProperties = TypePropertiesCache(type);
            var keyProperties = KeyPropertiesCache(type);
            var insertableKeyProperties = InsertableKeyPropertiesCache(type);
            var nonInsertableKeyProperties = keyProperties.Except(insertableKeyProperties);
            var computedProperties = ComputedPropertiesCache(type);
            var allPropertiesExceptNonInsertKeyAndComputed = allProperties.Except(nonInsertableKeyProperties.Union(computedProperties)).ToList();

            for (var i = 0; i < allPropertiesExceptNonInsertKeyAndComputed.Count; i++)
            {
                var property = allPropertiesExceptNonInsertKeyAndComputed.ElementAt(i);
                sqlAdapter.AppendColumnName(sbColumnList, property.Name);
                if (i < allPropertiesExceptNonInsertKeyAndComputed.Count - 1)
                    sbColumnList.Append(", ");
            }

            var sbParameterList = new StringBuilder(null);
            for (var i = 0; i < allPropertiesExceptNonInsertKeyAndComputed.Count; i++)
            {
                var property = allPropertiesExceptNonInsertKeyAndComputed.ElementAt(i);
                sbParameterList.AppendFormat("@{0}", property.Name);
                if (i < allPropertiesExceptNonInsertKeyAndComputed.Count - 1)
                    sbParameterList.Append(", ");
            }

            if (!isList)    //single entity
            {
                return sqlAdapter.InsertAsync(connection, transaction, commandTimeout, name, sbColumnList.ToString(),
                    sbParameterList.ToString(), keyProperties, entityToInsert);
            }

            //insert list of entities
            var cmd = $"INSERT INTO {name} ({sbColumnList}) values ({sbParameterList})";
            return connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout);
        }

        /// <summary>
        /// Updates entity in table "Ts" asynchronously using .NET 4.5 Task, checks if the entity is modified if the entity is tracked by the Get() extension.
        /// </summary>
        /// <typeparam name="T">Type to be updated</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToUpdate">Entity to be updated</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if updated, false if not found or not modified (tracked entities)</returns>
        public static async Task<bool> UpdateAsync<T>(this IDbConnection connection, T entityToUpdate, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var proxy = entityToUpdate as IProxy;
            if (proxy != null)
            {
                if (!proxy.IsDirty) return false;
            }

            var type = typeof(T);

            if (type.IsArray || type.IsGenericType())
                type = type.GetGenericArguments()[0];

            var keyProperties = KeyPropertiesCache(type);
            var explicitKeyProperties = ExplicitKeyPropertiesCache(type);
            if (!keyProperties.Any() && !explicitKeyProperties.Any())
                throw new ArgumentException("Entity must have at least one [Key] or [ExplicitKey] property");

            var name = GetTableName(type);

            var sb = new StringBuilder();
            sb.AppendFormat("update {0} set ", name);

            var allProperties = TypePropertiesCache(type);
            keyProperties.AddRange(explicitKeyProperties);
            var computedProperties = ComputedPropertiesCache(type);
            var nonIdProps = allProperties.Except(keyProperties.Union(computedProperties)).ToList();

            var adapter = GetFormatter(connection);

            for (var i = 0; i < nonIdProps.Count; i++)
            {
                var property = nonIdProps.ElementAt(i);
                adapter.AppendColumnNameEqualsValue(sb, property.Name);
                if (i < nonIdProps.Count - 1)
                    sb.AppendFormat(", ");
            }
            sb.Append(" where ");
            for (var i = 0; i < keyProperties.Count; i++)
            {
                var property = keyProperties.ElementAt(i);
                adapter.AppendColumnNameEqualsValue(sb, property.Name);
                if (i < keyProperties.Count - 1)
                    sb.AppendFormat(" and ");
            }
            var updated = await connection.ExecuteAsync(sb.ToString(), entityToUpdate, commandTimeout: commandTimeout, transaction: transaction).ConfigureAwait(false);
            return updated > 0;
        }

        /// <summary>
        /// Delete entity in table "Ts" asynchronously using .NET 4.5 Task.
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToDelete">Entity to delete</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if not found</returns>
        public static async Task<bool> DeleteAsync<T>(this IDbConnection connection, T entityToDelete, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            if (entityToDelete == null)
                throw new ArgumentException("Cannot Delete null Object", nameof(entityToDelete));

            var type = typeof(T);

            if (type.IsArray || type.IsGenericType())
                type = type.GetGenericArguments()[0];

            var keyProperties = KeyPropertiesCache(type);
            var explicitKeyProperties = ExplicitKeyPropertiesCache(type);
            if (!keyProperties.Any() && !explicitKeyProperties.Any())
                throw new ArgumentException("Entity must have at least one [Key] or [ExplicitKey] property");

            var name = GetTableName(type);
            keyProperties.AddRange(explicitKeyProperties);

            var sb = new StringBuilder();
            sb.AppendFormat("DELETE FROM {0} WHERE ", name);

            for (var i = 0; i < keyProperties.Count; i++)
            {
                var property = keyProperties.ElementAt(i);
                sb.AppendFormat("{0} = @{1}", property.Name, property.Name);
                if (i < keyProperties.Count - 1)
                    sb.AppendFormat(" AND ");
            }
            var deleted = await connection.ExecuteAsync(sb.ToString(), entityToDelete, transaction, commandTimeout).ConfigureAwait(false);
            return deleted > 0;
        }

        /// <summary>
        /// Delete all entities in the table related to the type T asynchronously using .NET 4.5 Task.
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the defualt) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if none found</returns>
        public static async Task<bool> DeleteAllAsync<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var type = typeof(T);
            var statement = "DELETE FROM " + GetTableName(type);
            var deleted = await connection.ExecuteAsync(statement, null, transaction, commandTimeout).ConfigureAwait(false);
            return deleted > 0;
        }
    }
}

public partial interface ISqlAdapter
{
    Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, String tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert);
}

public partial class SqlServerAdapter
{
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, String tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) values ({parameterList}); SELECT SCOPE_IDENTITY() id";
        var multi = await connection.QueryMultipleAsync(cmd, entityToInsert, transaction, commandTimeout);

        var first = multi.Read().FirstOrDefault();
        if (first == null || first.id == null) return 0;

        var id = (int)first.id;
        var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (!pi.Any()) return id;

        var idp = pi.First();
        idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

        return id;
    }
}

public partial class SqlCeServerAdapter
{
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        await connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);
        var r = (await connection.QueryAsync<dynamic>("SELECT @@IDENTITY id", transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false)).ToList();

        if (r.First() == null || r.First().id == null) return 0;
        var id = (int)r.First().id;

        var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (!pi.Any()) return id;

        var idp = pi.First();
        idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

        return id;
    }
}

public partial class MySqlAdapter
{
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName,
        string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        await connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);
        var r = await connection.QueryAsync<dynamic>("SELECT LAST_INSERT_ID() id", transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false);

        var id = r.First().id;
        if (id == null) return 0;
        var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (!pi.Any()) return Convert.ToInt32(id);

        var idp = pi.First();
        idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

        return Convert.ToInt32(id);
    }
}

public partial class PostgresAdapter
{ 
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("INSERT INTO {0} ({1}) VALUES ({2})", tableName, columnList, parameterList);

        // If no primary key then safe to assume a join table with not too much data to return
        var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (!propertyInfos.Any())
            sb.Append(" RETURNING *");
        else
        {
            sb.Append(" RETURNING ");
            bool first = true;
            foreach (var property in propertyInfos)
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(property.Name);
            }
        }

        var results = await connection.QueryAsync(sb.ToString(), entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        // Return the key by assinging the corresponding property in the object - by product is that it supports compound primary keys
        var id = 0;
        var values = results.First();
        foreach (var p in propertyInfos)
        {
            var value = values[p.Name.ToLower()];
            p.SetValue(entityToInsert, value, null);
            if (id == 0)
                id = Convert.ToInt32(value);
        }
        return id;
    }
}

public partial class SQLiteAdapter
{
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList}); SELECT last_insert_rowid() id";
        var multi = await connection.QueryMultipleAsync(cmd, entityToInsert, transaction, commandTimeout);

        var id = (int)multi.Read().First().id;
        var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (!pi.Any()) return id;

        var idp = pi.First();
        idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

        return id;
    }
}
#endif
