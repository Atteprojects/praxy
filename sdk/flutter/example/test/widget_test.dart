import 'package:flutter_test/flutter_test.dart';
import 'package:praxy_example/main.dart';

void main() {
  testWidgets('shows setup instructions when no --dart-define config is passed', (tester) async {
    await tester.pumpWidget(const PraxyExampleApp());
    await tester.pump();

    expect(find.textContaining('PRAXY_PROJECT_ID'), findsOneWidget);
  });
}
