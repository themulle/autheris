namespace Autheris.Application.SchemaRegistry;

using HotChocolate.Language;

public interface ISchemaLinter
{
    SchemaDiffResult Compare(string baselineSdl, string targetSdl);
    SchemaDiffResult Compare(DocumentNode baselineDoc, DocumentNode targetDoc);
}
