import { tableRef } from "@praxy/core";
import { databaseId, todosTableId } from "./config";
import type { Todo } from "./db.generated";

export type { Todo } from "./db.generated";
export { TodoColumns } from "./db.generated";

export const todosTable = tableRef<Todo>(databaseId, todosTableId);
