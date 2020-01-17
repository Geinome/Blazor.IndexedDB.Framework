using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Blazor.IndexedDB.Framework
{

    public abstract class IndexedSet 
    {
        abstract internal IEnumerable<IndexedEntity> GetChanged();
    }

    public class IndexedSet<T> : IndexedSet, IEnumerable<T> where T : new()
    {
        /// <summary>
        /// The internal stored items
        /// </summary>
        private readonly IList<IndexedEntity<T>> internalItems;
        /// <summary>
        /// The type T primary key, only != null if at least once requested by remove
        /// </summary>
        private PropertyInfo primaryKey;

        // ToDo: Remove PK dependency
        public IndexedSet(IEnumerable<T> records, PropertyInfo primaryKey)
        {
            this.primaryKey = primaryKey;
            this.internalItems = new List<IndexedEntity<T>>();

            if (records == null)
            {
                return;
            }


            foreach (var item in records)
            {
                var indexedItem = new IndexedEntity<T>(item)
                {
                    State = EntityState.Unchanged
                };

                this.internalItems.Add(indexedItem);
            }

        }

        public bool IsReadOnly => false;

        public int Count => this.Count();

        public void Add(T item)
        {
            if (!this.internalItems.Select(x => x.Instance).Contains(item))
            {
                this.internalItems.Add(new IndexedEntity<T>(item)
                {
                    State = EntityState.Added
                });
            }
        }

        public void Clear()
        {
            foreach (var item in this)
            {
                this.Remove(item);
            }
        }

        public bool Contains(T item)
        {
            return Enumerable.Contains(this, item);
        }

        public bool Remove(T item)
        {
            var internalItem = this.internalItems.FirstOrDefault(x => x.Instance.Equals(item));

            if (internalItem != null)
            {
                internalItem.State = EntityState.Deleted;

                return true;
            }
            // If reference was lost search for pk, increases the required time
            else
            {

                var value = this.primaryKey.GetValue(item);

                internalItem = this.internalItems.FirstOrDefault(x => this.primaryKey.GetValue(x.Instance).Equals(value));

                if (internalItem != null)
                {

                    internalItem.State = EntityState.Deleted;

                    return true;
                }
            }

            return false;
        }

        public IEnumerator<T> GetEnumerator()
        {
            return this.internalItems.Select(x => x.Instance).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            var enumerator =  this.GetEnumerator();

            return enumerator;
        }

        // ToDo: replace change tracker with better alternative 
        internal override IEnumerable<IndexedEntity> GetChanged()
        {
            foreach (var item in this.internalItems)
            {
                item.DetectChanges();

                if (item.State == EntityState.Unchanged)
                {
                    continue;
                }

                yield return item;
            }
        }
    }
}
