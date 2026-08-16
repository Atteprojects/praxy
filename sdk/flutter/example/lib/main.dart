import 'package:flutter/material.dart';
import 'package:praxy_flutter/praxy_flutter.dart';

import 'db.dart';

const _endpoint = String.fromEnvironment('PRAXY_ENDPOINT', defaultValue: 'http://localhost:5090');
const _projectId = String.fromEnvironment('PRAXY_PROJECT_ID');

/// `flutter_web_auth_2`'s callback scheme — must match the Android
/// `CallbackActivity` intent-filter's `android:scheme` in AndroidManifest.xml.
const _oauthCallbackScheme = 'com.praxy.example';

final px = PraxyFlutter(endpoint: _endpoint, projectId: _projectId);

void main() {
  runApp(const PraxyExampleApp());
}

final class PraxyExampleApp extends StatelessWidget {
  const PraxyExampleApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'Praxy Example',
    theme: ThemeData(colorSchemeSeed: Colors.indigo, useMaterial3: true),
    home: (_projectId.isEmpty || Db.todos.databaseId.isEmpty || Db.todos.tableId.isEmpty)
        ? const _MissingConfigScreen()
        : const AuthGate(),
  );
}

final class _MissingConfigScreen extends StatelessWidget {
  const _MissingConfigScreen();

  @override
  Widget build(BuildContext context) => Scaffold(
    body: Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Text(
          'Run with:\n\n'
          'flutter run \\\n'
          '  --dart-define=PRAXY_PROJECT_ID=<id> \\\n'
          '  --dart-define=PRAXY_DATABASE_ID=<id> \\\n'
          '  --dart-define=PRAXY_TABLE_ID=<id>\n\n'
          '(add --dart-define=PRAXY_ENDPOINT=... if not http://localhost:5090)',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.bodyLarge,
        ),
      ),
    ),
  );
}

/// Resolves once at startup (and again after sign-in/out): a stored session that
/// still passes `account.get()` means the user is signed in, with zero extra
/// wiring for "kill and restart the app, still signed in" — the `SecureSessionStore`
/// under `px` is what makes that true.
final class AuthGate extends StatefulWidget {
  const AuthGate({super.key});

  @override
  State<AuthGate> createState() => _AuthGateState();
}

class _AuthGateState extends State<AuthGate> {
  late Future<AppUser?> _future = _loadUser();

  Future<AppUser?> _loadUser() async {
    try {
      return await px.account.get();
    } on PraxyAuthException {
      return null;
    }
  }

  void _refresh() => setState(() => _future = _loadUser());

  @override
  Widget build(BuildContext context) => FutureBuilder<AppUser?>(
    future: _future,
    builder: (context, snapshot) {
      if (snapshot.connectionState != ConnectionState.done) {
        return const Scaffold(body: Center(child: CircularProgressIndicator()));
      }
      final user = snapshot.data;
      return user == null
          ? SignInScreen(onSignedIn: _refresh)
          : TodosScreen(user: user, onSignedOut: _refresh);
    },
  );
}

final class SignInScreen extends StatefulWidget {
  const SignInScreen({required this.onSignedIn, super.key});

  final VoidCallback onSignedIn;

  @override
  State<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends State<SignInScreen> {
  final _email = TextEditingController();
  final _password = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await action();
      widget.onSignedIn();
    } on PraxyException catch (error) {
      setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Sign in — Praxy Example')),
    body: Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 360),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextField(
                controller: _email,
                decoration: const InputDecoration(labelText: 'Email'),
                keyboardType: TextInputType.emailAddress,
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _password,
                decoration: const InputDecoration(labelText: 'Password'),
                obscureText: true,
              ),
              const SizedBox(height: 20),
              if (_error != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
                ),
              FilledButton(
                onPressed: _busy
                    ? null
                    : () => _run(
                        () => px.account.createEmailSession(email: _email.text, password: _password.text),
                      ),
                child: const Text('Sign in'),
              ),
              const SizedBox(height: 8),
              OutlinedButton(
                onPressed: _busy
                    ? null
                    : () => _run(() => px.account.create(email: _email.text, password: _password.text)),
                child: const Text('Sign up'),
              ),
              const SizedBox(height: 20),
              const Divider(),
              const SizedBox(height: 12),
              FilledButton.icon(
                icon: const Icon(Icons.login),
                onPressed: _busy
                    ? null
                    : () => _run(
                        () => px.oauth.signInWithGoogle(callbackUrlScheme: _oauthCallbackScheme),
                      ),
                label: const Text('Sign in with Google'),
              ),
              if (_busy)
                const Padding(
                  padding: EdgeInsets.only(top: 24),
                  child: Center(child: CircularProgressIndicator()),
                ),
            ],
          ),
        ),
      ),
    ),
  );
}

final class TodosScreen extends StatefulWidget {
  const TodosScreen({required this.user, required this.onSignedOut, super.key});

  final AppUser user;
  final VoidCallback onSignedOut;

  @override
  State<TodosScreen> createState() => _TodosScreenState();
}

class _TodosScreenState extends State<TodosScreen> {
  // A REST snapshot plus a live realtime patch stream — this is the SDK's
  // headline realtime demo, exercised by the owner test's "watch an update
  // arrive from the console" step.
  late final Stream<RowList<Todo>> _liveTodos = px.tables.liveList(
    Db.todos,
    queries: [Query.orderDesc(TodoColumns.title)],
  );

  Future<void> _addTodo() async {
    final controller = TextEditingController();
    final title = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('New todo'),
        content: TextField(controller: controller, autofocus: true, decoration: const InputDecoration(labelText: 'Title')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(context, controller.text), child: const Text('Add')),
        ],
      ),
    );
    if (title == null || title.trim().isEmpty) return;

    await px.tables.create(
      Db.todos,
      rowId: Uid.unique(),
      data: Todo(title: title.trim(), done: false),
      permissions: [
        Permission.read(Role.user(widget.user.id)),
        Permission.update(Role.user(widget.user.id)),
        Permission.delete(Role.user(widget.user.id)),
      ],
    );
  }

  Future<void> _toggle(Todo todo) => px.tables.update(Db.todos, todo.id!, data: {'done': !todo.done});

  Future<void> _delete(Todo todo) => px.tables.delete(Db.todos, todo.id!);

  Future<void> _signOut() async {
    await px.account.deleteSession();
    widget.onSignedOut();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text('Todos — ${widget.user.email}'),
      actions: [
        StreamBuilder<PraxyConnectionState>(
          stream: px.realtime.connection,
          builder: (context, snapshot) {
            final state = snapshot.data ?? PraxyConnectionState.disconnected;
            return Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              child: Center(child: _ConnectionDot(state: state)),
            );
          },
        ),
        IconButton(icon: const Icon(Icons.logout), onPressed: _signOut, tooltip: 'Sign out'),
      ],
    ),
    body: StreamBuilder<RowList<Todo>>(
      stream: _liveTodos,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return Center(child: Text('Error: ${snapshot.error}'));
        }
        final rows = snapshot.data?.rows;
        if (rows == null) {
          return const Center(child: CircularProgressIndicator());
        }
        if (rows.isEmpty) {
          return const Center(child: Text('No todos yet — tap + to add one.'));
        }
        return ListView.builder(
          itemCount: rows.length,
          itemBuilder: (context, index) {
            final todo = rows[index];
            return Dismissible(
              key: ValueKey(todo.id),
              onDismissed: (_) => _delete(todo),
              background: Container(color: Theme.of(context).colorScheme.errorContainer),
              child: CheckboxListTile(
                title: Text(
                  todo.title,
                  style: todo.done ? const TextStyle(decoration: TextDecoration.lineThrough) : null,
                ),
                value: todo.done,
                onChanged: (_) => _toggle(todo),
              ),
            );
          },
        );
      },
    ),
    floatingActionButton: FloatingActionButton(onPressed: _addTodo, child: const Icon(Icons.add)),
  );
}

final class _ConnectionDot extends StatelessWidget {
  const _ConnectionDot({required this.state});

  final PraxyConnectionState state;

  @override
  Widget build(BuildContext context) {
    final color = switch (state) {
      PraxyConnectionState.connected => Colors.green,
      PraxyConnectionState.connecting || PraxyConnectionState.reconnecting => Colors.orange,
      PraxyConnectionState.disconnected => Colors.grey,
    };
    return Tooltip(
      message: 'Realtime: ${state.name}',
      child: Container(width: 10, height: 10, decoration: BoxDecoration(color: color, shape: BoxShape.circle)),
    );
  }
}
