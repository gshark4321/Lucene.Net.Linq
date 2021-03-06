﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Common.Logging;
using Lucene.Net.Documents;
using Lucene.Net.Linq.Mapping;
using Lucene.Net.Linq.ScalarResultHandlers;
using Lucene.Net.Linq.Search.Function;
using Lucene.Net.Linq.Transformation;
using Lucene.Net.Linq.Translation;
using Lucene.Net.Search;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.ExpressionTreeVisitors;

namespace Lucene.Net.Linq
{
    internal class LuceneQueryExecutor<TDocument> : LuceneQueryExecutorBase<TDocument>
    {
        private readonly ObjectLookup<TDocument> newItem;
        private readonly IDocumentMapper<TDocument> mapper;
        private readonly IDocumentKeyConverter keyConverter;

        public LuceneQueryExecutor(Context context, ObjectLookup<TDocument> newItem, IDocumentMapper<TDocument> mapper)
            : base(context)
        {
            this.newItem = newItem;
            this.mapper = mapper;
            this.keyConverter = mapper as IDocumentKeyConverter;
        }

        protected override TDocument ConvertDocument(Document doc, IQueryExecutionContext context)
        {
            var key = (IDocumentKey) null;
            if (keyConverter != null)
            {
                key = keyConverter.ToKey(doc);
            }

            var item = newItem(key);
            
            mapper.ToObject(doc, context, item);
            
            return item;
        }
        
        protected override TDocument ConvertDocumentForCustomBoost(Document doc)
        {
            return ConvertDocument(doc, new QueryExecutionContext());
        }

        protected override IDocumentKey GetDocumentKey(Document doc, IQueryExecutionContext context)
        {
            if (keyConverter != null)
            {
                return keyConverter.ToKey(doc);
            }

            var item = ConvertDocument(doc, context);

            return mapper.ToKey(item);
        }

        public override IFieldMappingInfo GetMappingInfo(string propertyName)
        {
            return mapper.GetMappingInfo(propertyName);
        }

        public override IEnumerable<string> AllProperties
        {
            get { return mapper.AllProperties; }
        }

        public override IEnumerable<string> KeyProperties
        {
            get { return mapper.KeyProperties; }
        }

        public override Query CreateMultiFieldQuery(string pattern)
        {
            return mapper.CreateMultiFieldQuery(pattern);
        }

        protected override void PrepareSearchSettings(IQueryExecutionContext context)
        {
            mapper.PrepareSearchSettings(context);
        }
    }

    internal abstract class LuceneQueryExecutorBase<TDocument> : IQueryExecutor, IFieldMappingInfoProvider
    {
        private readonly ILog Log = LogManager.GetLogger(typeof(LuceneQueryExecutorBase<>));

        private readonly Context context;
        
        protected LuceneQueryExecutorBase(Context context)
        {
            this.context = context;
        }

        public T ExecuteScalar<T>(QueryModel queryModel)
        {
            var luceneQueryModel = PrepareQuery(queryModel);

            var searcherHandle = CheckoutSearcher();

            using (searcherHandle)
            {
                var searcher = searcherHandle.Searcher;
                var skipResults = luceneQueryModel.SkipResults;
                var maxResults = Math.Min(luceneQueryModel.MaxResults, searcher.MaxDoc - skipResults);

                TopFieldDocs hits;

                if (maxResults > 0)
                {
                    var executionContext = new QueryExecutionContext(searcher, luceneQueryModel.Query, luceneQueryModel.Filter);
                    PrepareSearchSettings(executionContext);

                    hits = searcher.Search(executionContext.Query, executionContext.Filter, maxResults, luceneQueryModel.Sort);
                }
                else
                {
                    hits = new TopFieldDocs(0, new ScoreDoc[0], new SortField[0], 0);
                }

                var handler = ScalarResultHandlerRegistry.Instance.GetItem(luceneQueryModel.ResultSetOperator.GetType());

                return handler.Execute<T>(luceneQueryModel, hits);
            }
        }

        public T ExecuteSingle<T>(QueryModel queryModel, bool returnDefaultWhenEmpty)
        {
            var sequence = ExecuteCollection<T>(queryModel);

            return returnDefaultWhenEmpty ? sequence.SingleOrDefault() : sequence.Single();
        }

        public class ItemHolder
        {
            public TDocument Current { get; set; }
        }

        public IEnumerable<T> ExecuteCollection<T>(QueryModel queryModel)
        {
            var itemHolder = new ItemHolder();

            var currentItemExpression = Expression.Property(Expression.Constant(itemHolder), "Current");

            var luceneQueryModel = PrepareQuery(queryModel);

            var mapping = new QuerySourceMapping();
            mapping.AddMapping(queryModel.MainFromClause, currentItemExpression);
            queryModel.TransformExpressions(e => ReferenceReplacingExpressionTreeVisitor.ReplaceClauseReferences(e, mapping, throwOnUnmappedReferences: true));

            var projection = GetProjector<T>(queryModel);
            var projector = projection.Compile();

            var searcherHandle = CheckoutSearcher();

            using (searcherHandle)
            {
                var searcher = searcherHandle.Searcher;
                var skipResults = luceneQueryModel.SkipResults;
                var maxResults = Math.Min(luceneQueryModel.MaxResults, searcher.MaxDoc - skipResults);
                var query = luceneQueryModel.Query;

                var scoreFunction = luceneQueryModel.GetCustomScoreFunction<TDocument>();
                if (scoreFunction != null)
                {
                    query = new DelegatingCustomScoreQuery<TDocument>(query, ConvertDocumentForCustomBoost, scoreFunction);
                }

                var executionContext = new QueryExecutionContext(searcher, query, luceneQueryModel.Filter);

                PrepareSearchSettings(executionContext);

                var hits = searcher.Search(executionContext.Query, executionContext.Filter, maxResults + skipResults, luceneQueryModel.Sort);

                if (luceneQueryModel.Last)
                {
                    skipResults = hits.ScoreDocs.Length - 1;
                    if (skipResults < 0) yield break;
                }

                var tracker = luceneQueryModel.DocumentTracker as IRetrievedDocumentTracker<TDocument>;

                executionContext.Phase = QueryExecutionPhase.ConvertResults;
                executionContext.Hits = hits;

                foreach (var p in EnumerateHits(hits, executionContext, searcher, tracker, itemHolder, skipResults, projector)) yield return p;
            }
        }

        private IEnumerable<T> EnumerateHits<T>(TopDocs hits, QueryExecutionContext executionContext, Searchable searcher, IRetrievedDocumentTracker<TDocument> tracker, ItemHolder itemHolder, int skipResults, Func<TDocument, T> projector)
        {
            for (var i = skipResults; i < hits.ScoreDocs.Length; i++)
            {
                executionContext.CurrentHit = i;
                executionContext.CurrentScoreDoc = hits.ScoreDocs[i];

                var docNum = hits.ScoreDocs[i].Doc;
                var document = searcher.Doc(docNum);

                if (tracker == null)
                {
                    itemHolder.Current = ConvertDocument(document, executionContext);
                    yield return projector(itemHolder.Current);
                    continue;
                }

                var key = GetDocumentKey(document, executionContext);

                if (tracker.IsMarkedForDeletion(key))
                {
                    continue;
                }

                TDocument item;
                if (!tracker.TryGetTrackedDocument(key, out item))
                {
                    item = ConvertDocument(document, executionContext);
                    tracker.TrackDocument(key, item, document);
                }

                itemHolder.Current = item;
                yield return projector(itemHolder.Current);
            }
        }

        private ISearcherHandle CheckoutSearcher()
        {
            return context.CheckoutSearcher();
        }

        private LuceneQueryModel PrepareQuery(QueryModel queryModel)
        {
            QueryModelTransformer.TransformQueryModel(queryModel);

            var builder = new QueryModelTranslator(this, context);
            builder.Build(queryModel);
            
            Log.Debug(m => m("Lucene query: {0}", builder.Model));

            return builder.Model;
        }

        protected virtual Expression<Func<TDocument, T>> GetProjector<T>(QueryModel queryModel)
        {
            return Expression.Lambda<Func<TDocument, T>>(queryModel.SelectClause.Selector, Expression.Parameter(typeof(TDocument)));
        }

        public abstract IFieldMappingInfo GetMappingInfo(string propertyName);
        public abstract IEnumerable<string> AllProperties { get; }
        public abstract IEnumerable<string> KeyProperties { get; }
        public abstract Query CreateMultiFieldQuery(string pattern);

        protected abstract IDocumentKey GetDocumentKey(Document doc, IQueryExecutionContext context);
        protected abstract TDocument ConvertDocument(Document doc, IQueryExecutionContext context);
        protected abstract TDocument ConvertDocumentForCustomBoost(Document doc);
        protected abstract void PrepareSearchSettings(IQueryExecutionContext context);
    }
}