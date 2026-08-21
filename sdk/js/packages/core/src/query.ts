/**
 * `Query`/`Col<T>` are value objects, not pre-encoded strings — mirrors `praxy_core`'s
 * `query.dart` (see `docs/research/nextjs-sdk.md`'s landmines: `Query.equal()` returning a raw
 * string forces `Query.or()` to re-decode its own inputs; composing child `Query` values directly
 * avoids that). Each `Query` becomes one entry of a repeated `queries[]` query-string param.
 */

/** A typed reference to a table column, used only to carry the column name at the type level. */
export class Col<T> {
  constructor(readonly name: string) {}
  // Phantom usage so `T` isn't reported as unused by the compiler.
  declare private readonly __type: T;
}

export type QueryJson = {
  method: string;
  attribute?: string;
  values?: unknown[];
};

export class Query {
  private constructor(
    readonly method: string,
    readonly attribute?: string,
    readonly values: unknown[] = [],
    readonly children: Query[] = [],
  ) {}

  static equal<T>(col: Col<T>, value: T): Query {
    return new Query("equal", col.name, [encode(value)]);
  }

  static equalAny<T>(col: Col<T>, values: T[]): Query {
    return new Query("equal", col.name, values.map(encode));
  }

  static notEqual<T>(col: Col<T>, value: T): Query {
    return new Query("notEqual", col.name, [encode(value)]);
  }

  static lessThan<T>(col: Col<T>, value: T): Query {
    return new Query("lessThan", col.name, [encode(value)]);
  }

  static lessThanEqual<T>(col: Col<T>, value: T): Query {
    return new Query("lessThanEqual", col.name, [encode(value)]);
  }

  static greaterThan<T>(col: Col<T>, value: T): Query {
    return new Query("greaterThan", col.name, [encode(value)]);
  }

  static greaterThanEqual<T>(col: Col<T>, value: T): Query {
    return new Query("greaterThanEqual", col.name, [encode(value)]);
  }

  static between<T>(col: Col<T>, start: T, end: T): Query {
    return new Query("between", col.name, [encode(start), encode(end)]);
  }

  static isNull(col: Col<unknown>): Query {
    return new Query("isNull", col.name);
  }

  static isNotNull(col: Col<unknown>): Query {
    return new Query("isNotNull", col.name);
  }

  static startsWith(col: Col<string>, value: string): Query {
    return new Query("startsWith", col.name, [value]);
  }

  static endsWith(col: Col<string>, value: string): Query {
    return new Query("endsWith", col.name, [value]);
  }

  static contains<T>(col: Col<T>, value: T): Query {
    return new Query("contains", col.name, [encode(value)]);
  }

  /** Requires a fulltext index on `col` server-side. */
  static search(col: Col<string>, value: string): Query {
    return new Query("search", col.name, [value]);
  }

  static select(columns: Col<unknown>[]): Query {
    return new Query("select", undefined, columns.map((c) => c.name));
  }

  static orderAsc(col: Col<unknown>): Query {
    return new Query("orderAsc", col.name);
  }

  static orderDesc(col: Col<unknown>): Query {
    return new Query("orderDesc", col.name);
  }

  static limit(count: number): Query {
    return new Query("limit", undefined, [count]);
  }

  static offset(count: number): Query {
    return new Query("offset", undefined, [count]);
  }

  static cursorAfter(rowId: string): Query {
    return new Query("cursorAfter", undefined, [rowId]);
  }

  static cursorBefore(rowId: string): Query {
    return new Query("cursorBefore", undefined, [rowId]);
  }

  static and(children: Query[]): Query {
    return new Query("and", undefined, [], children);
  }

  static or(children: Query[]): Query {
    return new Query("or", undefined, [], children);
  }

  /** Escape hatch for a method this builder doesn't wrap yet — still a real value object. */
  static raw(method: string, options: { attribute?: string; values?: unknown[] } = {}): Query {
    return new Query(method, options.attribute, options.values ?? []);
  }

  toJSON(): QueryJson {
    const json: QueryJson = { method: this.method };
    if (this.attribute !== undefined) json.attribute = this.attribute;
    if (this.children.length > 0) {
      json.values = this.children.map((c) => c.toJSON());
    } else if (this.values.length > 0) {
      json.values = this.values;
    }
    return json;
  }

  /** One entry of the repeated `queries[]` query-string param. */
  encode(): string {
    return JSON.stringify(this.toJSON());
  }
}

function encode(value: unknown): unknown {
  return value instanceof Date ? value.toISOString() : value;
}
