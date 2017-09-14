﻿using Chloe.Descriptors;
using Chloe.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Chloe
{
    public static class QueryExtension
    {
        public static IQuery<TSource> WhereIfNotNullOrEmpty<TSource>(this IQuery<TSource> source, string value, Expression<Func<TSource, bool>> predicate)
        {
            return source.WhereIf(!string.IsNullOrEmpty(value), predicate);
        }

        public static IQuery<TSource> WhereIfNotNull<TSource, TValue>(this IQuery<TSource> source, Nullable<TValue> value, Expression<Func<TSource, bool>> predicate) where TValue : struct
        {
            return source.WhereIf(value != null, predicate);
        }

        public static IQuery<TSource> WhereIfNotNull<TSource>(this IQuery<TSource> source, object value, Expression<Func<TSource, bool>> predicate)
        {
            return source.WhereIf(value != null, predicate);
        }

        public static IQuery<TSource> WhereIf<TSource>(this IQuery<TSource> source, bool condition, Expression<Func<TSource, bool>> predicate)
        {
            if (condition)
            {
                return source.Where(predicate);
            }
            return source;
        }

        public static IQuery<TSource> WhereIfNotNull<TSource, V>(this IQuery<TSource> source, V val, Expression<Func<TSource, V, bool>> predicate)
        {
            if (val != null)
            {
                Expression<Func<TSource, bool>> newPredicate = (Expression<Func<TSource, bool>>)ParameterTwoExpressionReplacer.Replace(predicate, val);
                source = source.Where(newPredicate);
            }

            return source;
        }
        public static IQuery<TSource> WhereIfNotNullOrEmpty<TSource>(this IQuery<TSource> source, string val, Expression<Func<TSource, string, bool>> predicate)
        {
            return source.WhereIfNotNull(val == string.Empty ? null : val, predicate);
        }

        /// <summary>
        /// dbContext.Query&lt;User&gt;().ToList&lt;User, UserModel&gt;();
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TModel"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        public static List<TModel> ToList<TEntity, TModel>(this IQuery<TEntity> source)
        {
            return source.MapTo<TEntity, TModel>().ToList();
        }
        /// <summary>
        /// dbContext.Query&lt;User&gt;().MapTo&lt;User, UserModel&gt;().ToList();
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <typeparam name="TModel"></typeparam>
        /// <param name="source"></param>
        /// <returns></returns>
        public static IQuery<TModel> MapTo<TEntity, TModel>(this IQuery<TEntity> source)
        {
            /*
             * Usage:
             * dbContext.Query<User>().MapTo<User, UserModel>().ToList();
             */

            /*
             * 根据 TEntity 与 TModel 属性对应关系构建 selector 表达式树，最后调用 Select() 方法
             * dbContext.Query<User>().Select(a => new UserModel() { Id = a.Id, Name = a.Name });
             * ps: 只支持简单的映射，不支持复杂的对应关系
             */

            List<MemberBinding> bindings = new List<MemberBinding>();

            Type modelType = typeof(TModel);
            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(typeof(TEntity));
            var mappingMemberDescriptors = typeDescriptor.MappingMemberDescriptors.Select(a => a.Value).ToDictionary(a => a.MemberInfo.Name, a => a);

            var props = modelType.GetProperties();
            ParameterExpression parameter = Expression.Parameter(typeDescriptor.EntityType, "a");
            foreach (var prop in props)
            {
                if (prop.GetSetMethod() == null)
                    continue;

                MappingMemberDescriptor mapMemberDescriptor;
                if (mappingMemberDescriptors.TryGetValue(prop.Name, out mapMemberDescriptor) == false)
                {
                    continue;
                }

                Expression sourceMemberAccess = Expression.MakeMemberAccess(parameter, mapMemberDescriptor.MemberInfo);
                if (prop.PropertyType != mapMemberDescriptor.MemberInfoType)
                {
                    sourceMemberAccess = Expression.Convert(sourceMemberAccess, prop.PropertyType);
                }

                MemberAssignment bind = Expression.Bind(prop, sourceMemberAccess);
                bindings.Add(bind);
            }

            NewExpression newExp = Expression.New(modelType);
            Expression selectorBody = Expression.MemberInit(newExp, bindings);
            Expression<Func<TEntity, TModel>> selector = Expression.Lambda<Func<TEntity, TModel>>(selectorBody, parameter);

            return source.Select(selector);
        }

        /// <summary>
        /// dbContext.Query&lt;User&gt;().OrderBy("Id asc,Age desc");
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="q"></param>
        /// <param name="orderString">Id asc,Age desc...</param>
        /// <returns></returns>
        public static IOrderedQuery<T> OrderBy<T>(this IQuery<T> q, string orderString)
        {
            if (q == null)
                throw new ArgumentNullException("q");
            if (string.IsNullOrEmpty(orderString))
                throw new ArgumentNullException("orderString");

            List<Ordering> orderingList = SplitOrderingString(orderString);

            IOrderedQuery<T> orderedQuery = null;
            for (int i = 0; i < orderingList.Count; i++)
            {
                Ordering ordering = orderingList[i];
                if (orderedQuery == null)
                    orderedQuery = q.InnerOrderBy(ordering);
                else
                    orderedQuery = orderedQuery.InnerThenBy(ordering);
            }

            return orderedQuery;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="q"></param>
        /// <param name="orderString">Id asc,Age desc...</param>
        /// <returns></returns>
        public static IOrderedQuery<T> ThenBy<T>(this IOrderedQuery<T> q, string orderString)
        {
            if (q == null)
                throw new ArgumentNullException("q");
            if (string.IsNullOrEmpty(orderString))
                throw new ArgumentNullException("orderString");

            List<Ordering> orderingList = SplitOrderingString(orderString);

            IOrderedQuery<T> orderedQuery = q;
            for (int i = 0; i < orderingList.Count; i++)
            {
                Ordering ordering = orderingList[i];
                orderedQuery = orderedQuery.InnerThenBy(ordering);
            }

            return orderedQuery;
        }

        static IOrderedQuery<T> InnerOrderBy<T>(this IQuery<T> q, Ordering ordering)
        {
            LambdaExpression keySelector = ConvertToLambda<T>(ordering.MemberChain);

            MethodInfo orderMethod;
            if (ordering.OrderType == OrderType.Asc)
                orderMethod = typeof(IQuery<T>).GetMethod("OrderBy");
            else
                orderMethod = typeof(IQuery<T>).GetMethod("OrderByDesc");

            IOrderedQuery<T> orderedQuery = Invoke<T>(q, orderMethod, keySelector);
            return orderedQuery;
        }
        static IOrderedQuery<T> InnerThenBy<T>(this IOrderedQuery<T> q, Ordering ordering)
        {
            LambdaExpression keySelector = ConvertToLambda<T>(ordering.MemberChain);

            MethodInfo orderMethod;
            if (ordering.OrderType == OrderType.Asc)
                orderMethod = typeof(IOrderedQuery<T>).GetMethod("ThenBy");
            else
                orderMethod = typeof(IOrderedQuery<T>).GetMethod("ThenByDesc");

            IOrderedQuery<T> orderedQuery = Invoke<T>(q, orderMethod, keySelector);
            return orderedQuery;
        }
        static IOrderedQuery<T> Invoke<T>(object q, MethodInfo orderMethod, LambdaExpression keySelector)
        {
            orderMethod = orderMethod.MakeGenericMethod(new Type[] { keySelector.Body.Type });
            IOrderedQuery<T> orderedQuery = (IOrderedQuery<T>)orderMethod.Invoke(q, new object[] { keySelector });
            return orderedQuery;
        }
        static List<Ordering> SplitOrderingString(string orderString)
        {
            string[] orderings = SplitWithRemoveEmptyEntries(orderString, ',');
            List<Ordering> orderingList = new List<Ordering>(orderings.Length);

            for (int i = 0; i < orderings.Length; i++)
            {
                orderingList.Add(Ordering.Create(orderings[i]));
            }

            return orderingList;
        }
        static LambdaExpression ConvertToLambda<T>(string memberChain)
        {
            Type entityType = typeof(T);

            string[] memberNames = SplitWithRemoveEmptyEntries(memberChain, '.');

            Type currType = entityType;
            ParameterExpression parameterExp = Expression.Parameter(entityType, "a");
            Expression exp = parameterExp;
            for (int i = 0; i < memberNames.Length; i++)
            {
                var memberName = memberNames[i];

                MemberInfo memberIfo = currType.GetProperty(memberName);
                if (memberIfo == null)
                {
                    memberIfo = currType.GetField(memberName);

                    if (memberIfo == null)
                    {
                        memberIfo = currType.GetProperties().Where(a => a.Name.ToLower() == memberName).FirstOrDefault();

                        if (memberIfo == null)
                        {
                            memberIfo = currType.GetFields().Where(a => a.Name.ToLower() == memberName).FirstOrDefault();
                        }
                    }
                }

                if (memberIfo == null)
                    throw new ArgumentException(string.Format("The type '{0}' doesn't define property or field '{1}'", currType.FullName, memberName));

                exp = Expression.MakeMemberAccess(exp, memberIfo);
                currType = exp.Type;
            }

            if (exp == parameterExp)
                throw new Exception("Oh,god!You are so lucky!");

            Type delegateType = null;

            delegateType = typeof(Func<,>).MakeGenericType(new Type[] { typeof(T), exp.Type });

            LambdaExpression lambda = Expression.Lambda(delegateType, exp, parameterExp);

            return lambda;
        }

        static string[] SplitWithRemoveEmptyEntries(string str, char c)
        {
            string[] arr = str.Split(new char[] { c }, StringSplitOptions.RemoveEmptyEntries);
            return arr;
        }

        class Ordering
        {
            public string MemberChain { get; set; }
            public OrderType OrderType { get; set; }

            public static Ordering Create(string str)
            {
                string[] arr = SplitWithRemoveEmptyEntries(str, ' ');

                Ordering ordering = new Ordering();

                if (arr.Length == 1)
                {
                    ordering.OrderType = OrderType.Asc;
                    ordering.MemberChain = arr[0];
                }
                else if (arr.Length == 2)
                {
                    string orderTypeString = arr[1].ToLower();
                    if (orderTypeString == "asc")
                        ordering.OrderType = OrderType.Asc;
                    else if (orderTypeString == "desc")
                        ordering.OrderType = OrderType.Desc;
                    else
                        throw new NotSupportedException(string.Format("Invalid order type '{0}'", orderTypeString));

                    ordering.MemberChain = arr[0];
                }
                else
                    throw new ArgumentException(string.Format("Invalid order text '{0}'", str));

                return ordering;
            }
        }
        enum OrderType
        {
            Asc,
            Desc
        }
    }
}
