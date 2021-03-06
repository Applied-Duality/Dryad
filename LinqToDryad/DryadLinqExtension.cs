/*
Copyright (c) Microsoft Corporation

All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in 
compliance with the License.  You may obtain a copy of the License 
at http://www.apache.org/licenses/LICENSE-2.0   


THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER 
EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF 
TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.  


See the Apache Version 2.0 License for specific language governing permissions and 
limitations under the License. 

*/

//
// � Microsoft Corporation.  All rights reserved.
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Linq.Expressions;
using System.Linq;
using Microsoft.Research.DryadLinq.Internal;

namespace Microsoft.Research.DryadLinq
{
    /// <summary>
    /// This provides some useful classes and operators that are commonly used
    /// in applications. The operators are defined using DryadLINQ operators.
    /// </summary>
    [Serializable]
    public struct Pair<T1, T2> : IEquatable<Pair<T1, T2>>
    {
        private T1 m_key;
        private T2 m_value;

        [FieldMapping("x", "Key")]
        [FieldMapping("y", "Value")]
        public Pair(T1 x, T2 y)
        {
            this.m_key = x;
            this.m_value = y;
        }

        public T1 Key
        {
            get { return this.m_key; }
        }

        public T2 Value
        {
            get { return this.m_value; }
        }

        public override bool Equals(Object obj)
        {
            if (!(obj is Pair<T1, T2>)) return false;
            Pair<T1, T2> pair = (Pair<T1, T2>)obj;
            return this.m_key.Equals(pair.Key) && this.m_value.Equals(pair.Value);
        }

        public bool Equals(Pair<T1, T2> val)
        {
            return this.m_key.Equals(val.Key) && this.m_value.Equals(val.Value);
        }

        public static bool Equals(Pair<T1, T2> a, Pair<T1, T2> b)
        {
            return a.Equals(b);
        }

        public static bool operator ==(Pair<T1, T2> a, Pair<T1, T2> b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Pair<T1, T2> a, Pair<T1, T2> b)
        {
            return !a.Equals(b);
        }

        public override int GetHashCode()
        {
            return (-1521134295 * this.m_key.GetHashCode()) + this.m_value.GetHashCode();
        }

        public override string ToString()
        {
            return "<" + this.Key + ", " + this.Value + ">";
        }
    }

    public static class HpcLinqExtension
    {
        /// <summary>
        /// The standard MapReduce.
        /// </summary>
        /// <typeparam name="TSource">The type of the records of input dataset</typeparam>
        /// <typeparam name="TMap">The type of the resulting records of mapper</typeparam>
        /// <typeparam name="TKey">The type of the keys for hash exchange</typeparam>
        /// <typeparam name="TResult">The type of the resulting records of reducer</typeparam>
        /// <param name="source">The input dataset</param>
        /// <param name="mapper">The map function</param>
        /// <param name="keySelector">The key extraction function</param>
        /// <param name="reducer">The reduce function</param>
        /// <returns>The result dataset of MapReduce</returns>
        public static IQueryable<TResult>
            MapReduce<TSource, TMap, TKey, TResult>(
                           this IQueryable<TSource> source,
                           Expression<Func<TSource, IEnumerable<TMap>>> mapper,
                           Expression<Func<TMap, TKey>> keySelector,
                           Expression<Func<TKey, IEnumerable<TMap>, TResult>> reducer)
        {
            return source.SelectMany(mapper).GroupBy(keySelector, reducer);
        }

        /// <summary>
        /// Compute the cross product of two datasets. The function procFunc is applied to each
        /// pair of the cross product to form the output dataset.
        /// </summary>
        /// <typeparam name="T1">The type of the records of dataset source1</typeparam>
        /// <typeparam name="T2">The type of the records of dataset source2</typeparam>
        /// <typeparam name="T3">The type of the records of the result dataset</typeparam>
        /// <param name="source1">The first input dataset</param>
        /// <param name="source2">The second input dataset</param>
        /// <param name="procFunc">The function to apply to each pair of the cross product </param>
        /// <returns>The output dataset</returns>
        public static IQueryable<T3>
            CrossProduct<T1, T2, T3>(this IQueryable<T1> source1,
                                     IQueryable<T2> source2,
                                     Expression<Func<T1, T2, T3>> procFunc)
        {
            return source1.ApplyPerPartition(source2, (x_1, y_1) => HpcLinqHelper.Cross(x_1, y_1, procFunc), true);
        }

        /// <summary>
        /// Conditional DoWhile loop.
        /// </summary>
        /// <typeparam name="T">The type of the input records</typeparam>
        /// <param name="source">The input dataset</param>
        /// <param name="body">The code body of the DoWhile loop</param>
        /// <param name="cond">The termination condition of the DoWhile loop</param>
        /// <returns>The output dataset</returns>
        public static IQueryable<T>
            DoWhile<T>(this IQueryable<T> source,
                       Func<IQueryable<T>, IQueryable<T>> body,
                       Func<IQueryable<T>, IQueryable<T>, IQueryable<bool>> cond,
                       Int32 count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (count == 0) return source;

            IQueryable<T> before = source;
            while (true)
            {
                IQueryable<T> after = before;
                for (int i = 0; i < count; i++)
                {
                    after = body(after);
                }
                var more = cond(before, after);
                HpcLinqQueryable.SubmitAndWait(after, more);
                if (!more.Single()) return after;
                before = after;
            }
        }

        /// <summary>
        /// Conditional DoWhile loop.
        /// </summary>
        /// <typeparam name="T">The type of the input records</typeparam>
        /// <param name="source">The input dataset</param>
        /// <param name="body">The code body of the DoWhile loop</param>
        /// <param name="cond">The termination condition of the DoWhile loop</param>
        /// <returns>The output dataset</returns>
        public static IQueryable<T>
            DoWhile<T>(this IQueryable<T> source,
                       Func<IQueryable<T>, IQueryable<T>> body,
                       Func<IQueryable<T>, IQueryable<T>, IQueryable<bool>> cond)
        {
            IQueryable<T> before = source;
            while (true)
            {
                IQueryable<T> after = body(before);
                var more = cond(before, after);
                HpcLinqQueryable.SubmitAndWait(after, more);
                if (!more.Single()) return after;
                before = after;
            }
        }

        /// <summary>
        /// Broadcast a dataset to multiple partitions
        /// </summary>
        /// <typeparam name="T">The type of the input records</typeparam>
        /// <param name="source">The input dataset</param>
        /// <returns>The output dataset, which consists of multiple copies of source</returns>
        public static IQueryable<T> BroadCast<T>(this IQueryable<T> source)
        {
            return source.ApplyPerPartition(source, (x, y) => HpcLinqHelper.SelectSecond(x, y), true);
        }

        /// <summary>
        /// Broadcast a dataset to n partitions.
        /// </summary>
        /// <typeparam name="T">The type of the input records</typeparam>
        /// <param name="source">The input dataset</param>
        /// <returns>The output dataset, each partition of which is a copy of source</returns>
        public static IQueryable<T> BroadCast<T>(this IQueryable<T> source, int n)
        {
            var dummy = source.ApplyPerPartition(x => HpcLinqHelper.ValueZero(x))
                              .HashPartition(x => x, n);
            return dummy.ApplyPerPartition(source, (x, y) => HpcLinqHelper.SelectSecond(x, y), true);
        }

        /// <summary>
        /// Check if each partition of the input dataset is ordered.
        /// </summary>
        /// <typeparam name="TSource">The type of the records of the input dataset</typeparam>
        /// <typeparam name="TKey">The type of the keys on which ordering is based</typeparam>
        /// <param name="source">The input dataset</param>
        /// <param name="keySelector">The key extraction function</param>
        /// <param name="comparer">A Comparer on TKey to compare records</param>
        /// <param name="isDescending">True if the check is for descending</param>
        /// <returns>The same dataset as the input</returns>
        public static IQueryable<TSource>
            CheckOrderBy<TSource, TKey>(this IQueryable<TSource> source,
                                        Expression<Func<TSource, TKey>> keySelector,
                                        IComparer<TKey> comparer,
                                        bool isDescending)
        {
            return source.ApplyPerPartition(x_1 => HpcLinqHelper.CheckSort(x_1, keySelector, comparer, isDescending));
        }
    }
}
