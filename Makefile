.PHONY: help start stop restart status daemon-start daemon-stop daemon-restart daemon-status systemd-install systemd-start systemd-stop systemd-status test clean spawn-agent instantiate-agent list-agents

# Default target
help:
	@echo "Alpaca Trading Bot - Process Management"
	@echo ""
	@echo "Shell-based (setsid method):"
	@echo "  make start        - Start bot with setsid (survives disconnect)"
	@echo "  make stop         - Stop the bot"
	@echo "  make restart      - Restart the bot"
	@echo "  make status       - Check bot status"
	@echo ""
	@echo "Python daemon (double-fork):"
	@echo "  make daemon-start  - Start bot as daemon"
	@echo "  make daemon-stop   - Stop daemon"
	@echo "  make daemon-status - Check daemon status"
	@echo ""
	@echo "Systemd user service:"
	@echo "  make systemd-install   - Install systemd user service"
	@echo "  make systemd-start     - Start systemd service"
	@echo "  make systemd-stop      - Stop systemd service"
	@echo "  make systemd-status    - Check systemd service"
	@echo ""
	@echo "Agent spawning (with language standards):"
	@echo "  make spawn-agent TASK='description' - Spawn agent with British English rules"
	@echo ""
	@echo "Agent templates:"
	@echo "  make list-agents          - List available agent templates"
	@echo "  make instantiate-agent AGENT=python-dev - Create project agent from template"
	@echo ""
	@echo "Other:"
	@echo "  make test         - Run tests"
	@echo "  make clean        - Clean up"

# Shell-based (setsid) method - RECOMMENDED for simple use
start:
	./bot.sh start

stop:
	./bot.sh stop

restart:
	./bot.sh restart

status:
	./bot.sh status

# Python daemon method (double-fork technique)
daemon-start:
	.venv/bin/python daemon.py start

daemon-stop:
	.venv/bin/python daemon.py stop

daemon-restart:
	.venv/bin/python daemon.py restart

daemon-status:
	.venv/bin/python daemon.py status

# Systemd user service method (most robust)
systemd-install:
	@echo "Installing systemd user service..."
	mkdir -p ~/.config/systemd/user
	cp alpaca-bot.service ~/.config/systemd/user/
	systemctl --user daemon-reload
	@echo "Service installed. Use 'make systemd-start' to begin."

systemd-start:
	systemctl --user start alpaca-bot

systemd-stop:
	systemctl --user stop alpaca-bot

systemd-restart:
	systemctl --user restart alpaca-bot

systemd-status:
	systemctl --user status alpaca-bot

systemd-enable:
	systemctl --user enable alpaca-bot

systemd-disable:
	systemctl --user disable alpaca-bot

# Agent spawning with language standards
spawn-agent:
	@if [ -z "$(TASK)" ]; then \
		echo "Usage: make spawn-agent TASK='Your task description'"; \
		echo "Example: make spawn-agent TASK='Refactor order manager'"; \
		exit 1; \
	fi
	@./scripts/spawn-with-standards.sh "$(TASK)"

# Agent template instantiation
list-agents:
	@python /home/t-rox/.openclaw/agents/templates/instantiate-agent.py --list

instantiate-agent:
	@if [ -z "$(AGENT)" ]; then \
		echo "Usage: make instantiate-agent AGENT=python-dev"; \
		echo "Example: make instantiate-agent AGENT=business-analyst"; \
		echo ""; \
		echo "Available agents:"; \
		python /home/t-rox/.openclaw/agents/templates/instantiate-agent.py --list; \
		exit 1; \
	fi
	@python /home/t-rox/.openclaw/agents/templates/instantiate-agent.py $(AGENT)

# Development
test:
	.venv/bin/pytest tests/ -v

clean:
	find . -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
	find . -type f -name "*.pyc" -delete 2>/dev/null || true
	find . -type f -name ".coverage" -delete 2>/dev/null || true
	rm -rf data/alpaca_bot.pid 2>/dev/null || true
