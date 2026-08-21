/**
 * A typed reference to a table — bundles the two wire ids `TablesService` needs plus a type
 * witness for the row shape, mirroring `praxy_core`'s `TableRef<T>`
 * (`sdk/flutter/praxy_core/lib/src/row_codec.dart`). `@praxy/codegen` never emits one of these
 * with hardcoded ids baked in (ids differ per environment) — construct it by hand from
 * environment-configured ids, same as `sdk/flutter/example/lib/db.dart` does.
 */
export interface TableRef<T> {
  readonly databaseId: string;
  readonly tableId: string;
  // Phantom usage so `T` isn't reported as unused by the compiler.
  readonly __row?: T;
}

export function tableRef<T>(databaseId: string, tableId: string): TableRef<T> {
  return { databaseId, tableId };
}
