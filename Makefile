IMAGE ?= emage
PORT ?= 3233
DOTNET ?= $(HOME)/.dotnet/dotnet
export DOTNET_ROOT ?= $(HOME)/.dotnet

.PHONY: build test run docker-build up down logs ps seed

build:
	$(DOTNET) build Emage/Emage.csproj -c Release

test:
	$(DOTNET) test Emage.slnx -c Release

run:
	$(DOTNET) run --no-launch-profile --project Emage/Emage.csproj -c Release --urls=http://localhost:$(PORT)

docker-build:
	docker build -t $(IMAGE) -f Emage/Dockerfile .

up:
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f

ps:
	docker compose ps

seed:
	docker cp Emage/images/encrypted.j2k `docker compose ps -q`:/data/images/encrypted.j2k