import time
import urllib.request

from src.notifier import AlertNotifier


def test_notifier_send_success_slack(monkeypatch, caplog):
    # Monkeypatch urlopen to succeed
    class DummyResponse:
        pass

    def fake_urlopen(req, timeout=5):
        return DummyResponse()

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.test")
    ok = notifier.send_alert("Test", "message", severity="ERROR")
    assert ok is True
    assert "Slack alert sent" in caplog.text


def test_notifier_handles_permanent_failure_slack(monkeypatch, caplog):
    # Monkeypatch urlopen to always raise
    def fake_urlopen(req, timeout=5):
        raise RuntimeError("permanent failure")

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.test")
    ok = notifier.send_alert("Test", "message", severity="ERROR")
    assert ok is False
    assert ("Failed to send Slack alert" in caplog.text) or ("Failed to send alert" in caplog.text)


def test_notifier_retry_on_transient_failure(monkeypatch):
    # This project-level notifier has no internal retry; test a simple retry wrapper
    calls = {"n": 0}

    def flaky_urlopen(req, timeout=5):
        calls["n"] += 1
        if calls["n"] < 3:
            raise RuntimeError("transient")
        return object()

    monkeypatch.setattr(urllib.request, "urlopen", flaky_urlopen)

    # Speed up sleep by monkeypatching time.sleep to a no-op and count
    sleeps = {"n": 0}

    def fake_sleep(s):
        sleeps["n"] += 1

    monkeypatch.setattr(time, "sleep", fake_sleep)

    notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.test")

    # Now test internal retries in AlertNotifier itself
    notifier.retries = 3
    notifier.backoff = 0.01

    result = notifier.send_alert("RTest", "msg")
    assert result is True
    # two backoff sleeps should have been invoked before success
    assert sleeps["n"] == 2
