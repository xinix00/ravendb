// -----------------------------------------------------------------------
//  <copyright file="SetIndexPriority.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Xunit;

namespace SlowTests.Bugs.Indexing
{
    public class SetIndexPriority : RavenTestBase
    {
        private class FakeIndex : AbstractIndexCreationTask
        {
            public override IndexDefinition CreateIndexDefinition()
            {
                return new IndexDefinition()
                {
                    Maps = { "from d in docs select new { d.Id }" }
                };
            }
        }

        [Fact]
        public void changing_index_priority_needs_to_set_it_on_index_instance_as_well()
        {
            using (var store = GetDocumentStore())
            {
                new FakeIndex().Execute(store);

                foreach (var expected in new[] { IndexPriority.Normal, IndexPriority.High, IndexPriority.Low })
                {
                    store.Admin.Send(new SetIndexPriorityOperation("FakeIndex", expected));

                    var db = GetDocumentDatabaseInstanceFor(store).Result;
                    var indexInstance = db.IndexStore.GetIndex("FakeIndex");

                    Assert.Equal(expected, indexInstance.Definition.Priority);
                }
            }
        }
    }
}