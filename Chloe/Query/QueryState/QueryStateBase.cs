﻿using Chloe.DbExpressions;
using Chloe.Query.Mapping;
using Chloe.Query.QueryExpressions;
using Chloe.Query.Visitors;
using Chloe.Utility;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Linq;
using Chloe.Core.Visitors;

namespace Chloe.Query.QueryState
{
    abstract class QueryStateBase : IQueryState
    {
        ResultElement _resultElement;
        List<IMappingObjectExpression> _moeList = null;
        protected QueryStateBase(ResultElement resultElement)
        {
            this._resultElement = resultElement;
        }

        protected List<IMappingObjectExpression> MoeList
        {
            get
            {
                if (this._moeList == null)
                    this._moeList = new List<IMappingObjectExpression>(1) { this._resultElement.MappingObjectExpression };

                return this._moeList;
            }
        }

        public virtual ResultElement Result
        {
            get
            {
                return this._resultElement;
            }
        }

        public virtual IQueryState Accept(WhereExpression exp)
        {
            DbExpression dbExp = FilterPredicateExpressionVisitor.VisitFilterPredicate(exp.Expression, this.MoeList);
            this._resultElement.AppendCondition(dbExp);

            return this;
        }
        public virtual IQueryState Accept(OrderExpression exp)
        {
            if (exp.NodeType == QueryExpressionType.OrderBy || exp.NodeType == QueryExpressionType.OrderByDesc)
                this._resultElement.OrderSegments.Clear();

            DbOrderSegment orderSeg = VisistOrderExpression(this.MoeList, exp);

            if (this._resultElement.IsOrderSegmentsFromSubQuery)
            {
                this._resultElement.OrderSegments.Clear();
                this._resultElement.IsOrderSegmentsFromSubQuery = false;
            }

            this._resultElement.OrderSegments.Add(orderSeg);

            return this;
        }
        public virtual IQueryState Accept(SelectExpression exp)
        {
            ResultElement result = this.CreateNewResult(exp.Selector);
            return this.CreateQueryState(result);
        }
        public virtual IQueryState Accept(SkipExpression exp)
        {
            SkipQueryState state = new SkipQueryState(this.Result, exp.Count);
            return state;
        }
        public virtual IQueryState Accept(TakeExpression exp)
        {
            TakeQueryState state = new TakeQueryState(this.Result, exp.Count);
            return state;
        }
        public virtual IQueryState Accept(AggregateQueryExpression exp)
        {
            List<DbExpression> dbParameters = new List<DbExpression>(exp.Parameters.Count);
            foreach (Expression pExp in exp.Parameters)
            {
                var dbExp = GeneralExpressionVisitor.VisitPredicate((LambdaExpression)pExp, this.MoeList);
                dbParameters.Add(dbExp);
            }

            DbAggregateExpression dbAggregateExp = new DbAggregateExpression(exp.ElementType, exp.Method, dbParameters);
            MappingFieldExpression mfe = new MappingFieldExpression(exp.ElementType, dbAggregateExp);

            ResultElement result = new ResultElement();

            result.MappingObjectExpression = mfe;
            result.FromTable = this._resultElement.FromTable;
            result.AppendCondition(this._resultElement.Condition);

            AggregateQueryState state = new AggregateQueryState(result);
            return state;
        }
        public virtual IQueryState Accept(GroupingQueryExpression exp)
        {
            List<IMappingObjectExpression> moeList = this.MoeList;
            foreach (var item in exp.GroupPredicates)
            {
                var dbExp = GeneralExpressionVisitor.VisitPredicate(item, moeList);
                this._resultElement.GroupSegments.Add(dbExp);
            }

            foreach (var item in exp.HavingPredicates)
            {
                var dbExp = GeneralExpressionVisitor.VisitPredicate(item, moeList);
                this._resultElement.AppendHavingCondition(dbExp);
            }

            var newResult = this.CreateNewResult(exp.Selector);
            return new GroupingQueryState(newResult);
        }

        public virtual ResultElement CreateNewResult(LambdaExpression selector)
        {
            ResultElement result = new ResultElement();
            result.FromTable = this._resultElement.FromTable;

            IMappingObjectExpression r = SelectorExpressionVisitor.VisitSelectExpression(selector, this.MoeList);
            result.MappingObjectExpression = r;
            result.OrderSegments.AddRange(this._resultElement.OrderSegments);
            result.AppendCondition(this._resultElement.Condition);

            result.GroupSegments.AddRange(this._resultElement.GroupSegments);
            result.AppendHavingCondition(this._resultElement.HavingCondition);

            return result;
        }
        public virtual IQueryState CreateQueryState(ResultElement result)
        {
            return new GeneralQueryState(result);
        }

        public virtual MappingData GenerateMappingData()
        {
            MappingData data = new MappingData();

            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();

            var objectActivatorCreator = this._resultElement.MappingObjectExpression.GenarateObjectActivatorCreator(sqlQuery);

            data.SqlQuery = sqlQuery;
            data.ObjectActivatorCreator = objectActivatorCreator;

            return data;
        }

        public virtual GeneralQueryState AsSubQueryState()
        {
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            ResultElement result = new ResultElement();

            DbTableSegment tableSeg = new DbTableSegment(subQuery, result.GenerateUniqueTableAlias());
            DbFromTableExpression fromTable = new DbFromTableExpression(tableSeg);

            result.FromTable = fromTable;

            DbTable table = new DbTable(tableSeg.Alias);

            //TODO 根据旧的生成新 MappingMembers
            IMappingObjectExpression newMoe = this.Result.MappingObjectExpression.ToNewObjectExpression(sqlQuery, table);
            result.MappingObjectExpression = newMoe;

            //得将 subQuery.SqlQuery.Orders 告诉 以下创建的 result
            //将 orderPart 传递下去
            if (this.Result.OrderSegments.Count > 0)
            {
                for (int i = 0; i < this.Result.OrderSegments.Count; i++)
                {
                    DbOrderSegment orderSeg = this.Result.OrderSegments[i];
                    DbExpression orderExp = orderSeg.DbExpression;

                    string alias = null;

                    DbColumnSegment columnExpression = sqlQuery.ColumnSegments.Where(a => DbExpressionEqualityComparer.EqualsCompare(orderExp, a.Body)).FirstOrDefault();

                    // 对于重复的则不需要往 sqlQuery.Columns 重复添加了
                    if (columnExpression != null)
                    {
                        alias = columnExpression.Alias;
                    }
                    else
                    {
                        alias = Utils.GenerateUniqueColumnAlias(sqlQuery);
                        DbColumnSegment columnSeg = new DbColumnSegment(orderExp, alias);
                        sqlQuery.ColumnSegments.Add(columnSeg);
                    }

                    DbColumnAccessExpression columnAccessExpression = new DbColumnAccessExpression(orderExp.Type, table, alias);
                    result.OrderSegments.Add(new DbOrderSegment(columnAccessExpression, orderSeg.OrderType));
                }
            }

            result.IsOrderSegmentsFromSubQuery = true;

            GeneralQueryState queryState = new GeneralQueryState(result);
            return queryState;
        }
        public virtual DbSqlQueryExpression CreateSqlQuery()
        {
            DbSqlQueryExpression sqlQuery = new DbSqlQueryExpression();

            sqlQuery.Table = this._resultElement.FromTable;
            sqlQuery.OrderSegments.AddRange(this._resultElement.OrderSegments);
            sqlQuery.Condition = this._resultElement.Condition;

            sqlQuery.GroupSegments.AddRange(this._resultElement.GroupSegments);
            sqlQuery.HavingCondition = this._resultElement.HavingCondition;

            return sqlQuery;
        }

        protected static DbOrderSegment VisistOrderExpression(List<IMappingObjectExpression> moeList, OrderExpression orderExp)
        {
            DbExpression dbExpression = GeneralExpressionVisitor.VisitPredicate(orderExp.Expression, moeList);
            OrderType orderType;
            if (orderExp.NodeType == QueryExpressionType.OrderBy || orderExp.NodeType == QueryExpressionType.ThenBy)
            {
                orderType = OrderType.Asc;
            }
            else if (orderExp.NodeType == QueryExpressionType.OrderByDesc || orderExp.NodeType == QueryExpressionType.ThenByDesc)
            {
                orderType = OrderType.Desc;
            }
            else
                throw new NotSupportedException(orderExp.NodeType.ToString());

            DbOrderSegment orderSeg = new DbOrderSegment(dbExpression, orderType);

            return orderSeg;
        }

        public virtual FromQueryResult ToFromQueryResult()
        {
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            DbTableSegment tableSeg = new DbTableSegment(subQuery, UtilConstants.DefaultTableAlias);
            DbFromTableExpression fromTable = new DbFromTableExpression(tableSeg);

            DbTable table = new DbTable(tableSeg.Alias);
            IMappingObjectExpression newMoe = this.Result.MappingObjectExpression.ToNewObjectExpression(sqlQuery, table);

            FromQueryResult result = new FromQueryResult();
            result.FromTable = fromTable;
            result.MappingObjectExpression = newMoe;
            return result;
        }

        public virtual JoinQueryResult ToJoinQueryResult(JoinType joinType, LambdaExpression conditionExpression, DbFromTableExpression fromTable, List<IMappingObjectExpression> moeList, string tableAlias)
        {
            DbSqlQueryExpression sqlQuery = this.CreateSqlQuery();
            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);

            string alias = tableAlias;
            DbTableSegment tableSeg = new DbTableSegment(subQuery, alias);

            DbTable table = new DbTable(tableSeg.Alias);
            IMappingObjectExpression newMoe = this.Result.MappingObjectExpression.ToNewObjectExpression(sqlQuery, table);

            List<IMappingObjectExpression> moes = new List<IMappingObjectExpression>(moeList.Count + 1);
            moes.AddRange(moeList);
            moes.Add(newMoe);
            DbExpression condition = GeneralExpressionVisitor.VisitPredicate(conditionExpression, moes);

            DbJoinTableExpression joinTable = new DbJoinTableExpression(joinType, tableSeg, condition);

            JoinQueryResult result = new JoinQueryResult();
            result.MappingObjectExpression = newMoe;
            result.JoinTable = joinTable;
            return result;
        }
    }
}
