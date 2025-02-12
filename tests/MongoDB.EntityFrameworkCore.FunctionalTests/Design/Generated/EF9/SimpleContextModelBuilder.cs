// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#pragma warning disable 219, 612, 618
#nullable disable

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Design
{
    public partial class SimpleContextModel
    {
        private SimpleContextModel()
            : base(skipDetectChanges: false, modelId: new Guid("baa684fb-2923-4148-bc5a-47040d30899d"), entityTypeCount: 2)
        {
        }

        partial void Initialize()
        {
            var everyType = EveryTypeEntityType.Create(this);
            var ownedEntity = OwnedEntityEntityType.Create(this);

            OwnedEntityEntityType.CreateForeignKey1(ownedEntity, everyType);

            EveryTypeEntityType.CreateAnnotations(everyType);
            OwnedEntityEntityType.CreateAnnotations(ownedEntity);

            AddAnnotation("ProductVersion", "9.0.1");
        }
    }
}
