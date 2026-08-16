/// Builds `action("role")` permission strings (architecture.md §4.3,
/// research/appwrite-api.md's permission grammar, adopted verbatim). `write` is an
/// aggregate the server expands to create+update+delete — it is never returned.
abstract final class Permission {
  static String read(String role) => 'read("$role")';
  static String create(String role) => 'create("$role")';
  static String update(String role) => 'update("$role")';
  static String delete(String role) => 'delete("$role")';
  static String write(String role) => 'write("$role")';
}

/// Builds the role half of a permission string. One dimension max — a role is
/// never both a team role and a label, for instance.
abstract final class Role {
  static String any() => 'any';
  static String guests() => 'guests';
  static String users({bool verified = false}) => verified ? 'users/verified' : 'users';
  static String user(String id, {bool verified = false}) =>
      verified ? 'user:$id/verified' : 'user:$id';
  static String team(String id, [String? role]) => role == null ? 'team:$id' : 'team:$id/$role';
  static String member(String id) => 'member:$id';
  static String label(String name) => 'label:$name';
}
