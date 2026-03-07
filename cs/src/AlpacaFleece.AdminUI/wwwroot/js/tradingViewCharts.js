window.tradingViewCharts = (() => {
    const charts = new Map();

    const seriesDefinitions = {
        candlestick: LightweightCharts.CandlestickSeries,
        line: LightweightCharts.LineSeries,
        area: LightweightCharts.AreaSeries,
        baseline: LightweightCharts.BaselineSeries,
        bar: LightweightCharts.BarSeries,
        histogram: LightweightCharts.HistogramSeries
    };

    const defaultChartHeight = 400;

    function resolveTheme(options) {
        if (typeof options.isDarkTheme === "boolean") {
            return options.isDarkTheme ? "dark" : "light";
        }

        if (document.body.classList.contains("mud-theme-dark")) {
            return "dark";
        }

        return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }

    function getThemeColors(theme) {
        if (theme === "dark") {
            return {
                background: "#1a1a2e",
                text: "rgba(255,255,255,0.87)",
                grid: "rgba(255,255,255,0.10)",
                border: "rgba(255,255,255,0.12)",
                upCandle: "#00BFA5",
                downCandle: "#ef5350",
                volumeUp: "rgba(0,191,165,0.55)",
                volumeDown: "rgba(239,83,80,0.55)",
                line: "#4FC3F7"
            };
        }

        return {
            background: "#ffffff",
            text: "#1d1d1d",
            grid: "rgba(0,0,0,0.08)",
            border: "rgba(0,0,0,0.16)",
            upCandle: "#00897B",
            downCandle: "#D32F2F",
            volumeUp: "rgba(0,137,123,0.45)",
            volumeDown: "rgba(211,47,47,0.45)",
            line: "#1976D2"
        };
    }

    function toNumber(value, fallback = 0) {
        const number = Number(value);
        return Number.isFinite(number) ? number : fallback;
    }

    function resolveHeight(value) {
        return Math.max(toNumber(value, defaultChartHeight), 220);
    }

    function toTime(value) {
        const time = Number(value);
        if (!Number.isFinite(time) || time <= 0) {
            return 0;
        }

        return Math.floor(time);
    }

    function normalizeCandles(data) {
        return (data || [])
            .map((item) => ({
                time: toTime(item.time ?? item.Time),
                open: toNumber(item.open ?? item.Open),
                high: toNumber(item.high ?? item.High),
                low: toNumber(item.low ?? item.Low),
                close: toNumber(item.close ?? item.Close),
                volume: toNumber(item.volume ?? item.Volume)
            }))
            .filter((item) => item.time > 0)
            .sort((a, b) => a.time - b.time);
    }

    function buildMainSeriesData(mainSeriesType, candles) {
        const type = (mainSeriesType || "candlestick").toLowerCase();

        if (type === "candlestick" || type === "bar") {
            return candles.map((item) => ({
                time: item.time,
                open: item.open,
                high: item.high,
                low: item.low,
                close: item.close
            }));
        }

        return candles.map((item) => ({
            time: item.time,
            value: item.close
        }));
    }

    function buildVolumeData(candles, colors) {
        return candles.map((item) => ({
            time: item.time,
            value: item.volume,
            color: item.close >= item.open ? colors.volumeUp : colors.volumeDown
        }));
    }

    function buildSeriesMarkers(candles) {
        if (!candles || candles.length < 2) {
            return [];
        }

        let maxHigh = candles[0];
        let minLow = candles[0];

        for (const candle of candles) {
            if (candle.high > maxHigh.high) {
                maxHigh = candle;
            }
            if (candle.low < minLow.low) {
                minLow = candle;
            }
        }

        return [
            {
                time: maxHigh.time,
                position: "aboveBar",
                color: "#4FC3F7",
                shape: "arrowDown",
                text: "High"
            },
            {
                time: minLow.time,
                position: "belowBar",
                color: "#FFB300",
                shape: "arrowUp",
                text: "Low"
            }
        ];
    }

    function buildLocalizationOptions(locale, timeZone) {
        const resolvedLocale = locale || navigator.language || "en-US";
        const formatter = new Intl.DateTimeFormat(resolvedLocale, {
            hour12: false,
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            timeZone: timeZone || "UTC"
        });

        return {
            locale: resolvedLocale,
            timeFormatter: (time) => {
                if (typeof time === "number") {
                    return formatter.format(new Date(time * 1000));
                }

                if (time && typeof time === "object" && Number.isInteger(time.year)) {
                    return `${String(time.month).padStart(2, "0")}/${String(time.day).padStart(2, "0")}/${time.year}`;
                }

                return "";
            }
        };
    }

    function buildChartOptions(options, themeColors, container) {
        return {
            width: Math.max(container.clientWidth || 0, 240),
            height: resolveHeight(options.height),
            layout: {
                background: { color: themeColors.background },
                textColor: themeColors.text,
                fontFamily: "Inter, Roboto, sans-serif"
            },
            crosshair: {
                mode: LightweightCharts.CrosshairMode.Normal,
                horzLine: { visible: true, labelVisible: true },
                vertLine: { visible: true, labelVisible: true }
            },
            grid: {
                vertLines: { color: themeColors.grid },
                horzLines: { color: themeColors.grid }
            },
            rightPriceScale: {
                visible: options.showRightPriceScale !== false,
                borderColor: themeColors.border,
                autoScale: true
            },
            leftPriceScale: {
                visible: options.showLeftPriceScale === true,
                borderColor: themeColors.border,
                autoScale: true
            },
            timeScale: {
                visible: true,
                borderColor: themeColors.border,
                timeVisible: true,
                secondsVisible: false,
                rightOffset: 8,
                fixLeftEdge: false,
                fixRightEdge: false,
                lockVisibleTimeRangeOnResize: true,
                rightBarStaysOnScroll: true
            },
            localization: buildLocalizationOptions(options.locale, options.timeZone)
        };
    }

    function buildMainSeriesOptions(mainSeriesType, themeColors, options) {
        const type = (mainSeriesType || "candlestick").toLowerCase();

        if (type === "candlestick") {
            return {
                upColor: themeColors.upCandle,
                downColor: themeColors.downCandle,
                borderUpColor: themeColors.upCandle,
                borderDownColor: themeColors.downCandle,
                wickUpColor: themeColors.upCandle,
                wickDownColor: themeColors.downCandle,
                lastValueVisible: true,
                priceLineVisible: options.showPriceLine !== false,
                priceFormat: {
                    type: "price",
                    precision: toNumber(options.pricePrecision, 2),
                    minMove: toNumber(options.priceMinMove, 0.01)
                }
            };
        }

        if (type === "line") {
            return {
                color: themeColors.line,
                lineWidth: 2,
                lastValueVisible: true,
                priceLineVisible: options.showPriceLine !== false
            };
        }

        return {
            lastValueVisible: true,
            priceLineVisible: options.showPriceLine !== false
        };
    }

    function addSeries(chart, seriesType, seriesOptions, paneIndex) {
        const normalizedType = (seriesType || "candlestick").toLowerCase();
        const definition = seriesDefinitions[normalizedType] || seriesDefinitions.candlestick;

        if (typeof paneIndex === "number") {
            try {
                return chart.addSeries(definition, seriesOptions, paneIndex);
            } catch (error) {
                const message = String(error && error.message ? error.message : error);
                const unsupportedPaneError = /pane|argument|parameter|overload|expected/i.test(message);
                if (!unsupportedPaneError) {
                    throw error;
                }

                return chart.addSeries(definition, seriesOptions);
            }
        }

        return chart.addSeries(definition, seriesOptions);
    }

    function clearPriceLines(state) {
        if (!state.mainSeries || !state.priceLines || state.priceLines.length === 0) {
            return;
        }

        for (const line of state.priceLines) {
            state.mainSeries.removePriceLine(line);
        }

        state.priceLines = [];
    }

    function setMarkers(state, markers) {
        if (!state || !state.mainSeries) {
            return;
        }

        if (state.markersApi && typeof state.markersApi.setMarkers === "function") {
            state.markersApi.setMarkers(markers);
            return;
        }

        if (typeof state.mainSeries.setMarkers === "function") {
            state.mainSeries.setMarkers(markers);
            return;
        }

        if (typeof LightweightCharts.createSeriesMarkers === "function") {
            state.markersApi = LightweightCharts.createSeriesMarkers(state.mainSeries, markers || []);
        }
    }

    function updateData(chartId, options) {
        const state = charts.get(chartId);
        if (!state) {
            return false;
        }

        const candles = normalizeCandles(options.data);
        const mainData = buildMainSeriesData(state.mainSeriesType, candles);

        state.mainSeries.setData(mainData);

        if (state.volumeSeries) {
            state.volumeSeries.setData(buildVolumeData(candles, state.themeColors));
        }

        if (options.showMarkers !== false) {
            setMarkers(state, buildSeriesMarkers(candles));
        } else {
            setMarkers(state, []);
        }

        clearPriceLines(state);
        if (options.showPriceLine !== false && candles.length > 0) {
            const lastCandle = candles[candles.length - 1];
            const lastLine = state.mainSeries.createPriceLine({
                price: lastCandle.close,
                color: state.themeColors.line,
                lineWidth: 1,
                lineStyle: LightweightCharts.LineStyle.Dashed,
                axisLabelVisible: true,
                title: "Last"
            });
            state.priceLines.push(lastLine);
        }

        if (options.autoFitContent !== false) {
            state.chart.timeScale().fitContent();
        }

        return true;
    }

    function normalizeSeriesType(value) {
        return (value || "candlestick").toLowerCase();
    }

    function shouldRecreate(state, options) {
        const requestedMainSeriesType = normalizeSeriesType(options.mainSeriesType);
        const requestedShowVolume = options.showVolume !== false;
        const requestedVolumeInSeparatePane = options.volumeInSeparatePane === true;

        return requestedMainSeriesType !== state.mainSeriesType
            || requestedShowVolume !== state.showVolume
            || requestedVolumeInSeparatePane !== state.volumeInSeparatePane;
    }

    function create(chartId, containerId, options) {
        const effectiveOptions = options || {};
        const container = document.getElementById(containerId);
        if (!container) {
            throw new Error(`TradingView chart container not found: ${containerId}`);
        }

        dispose(chartId);

        const theme = resolveTheme(effectiveOptions);
        const themeColors = getThemeColors(theme);
        const chart = LightweightCharts.createChart(container, buildChartOptions(effectiveOptions, themeColors, container));

        const mainSeriesType = normalizeSeriesType(effectiveOptions.mainSeriesType);
        const mainSeries = addSeries(chart, mainSeriesType, buildMainSeriesOptions(mainSeriesType, themeColors, effectiveOptions));

        let volumeSeries = null;
        const showVolume = effectiveOptions.showVolume !== false;
        const volumeInSeparatePane = effectiveOptions.volumeInSeparatePane === true;

        if (showVolume) {
            const volumeOptions = {
                priceScaleId: volumeInSeparatePane ? "" : "volume",
                priceFormat: { type: "volume" },
                lastValueVisible: false,
                priceLineVisible: false,
                scaleMargins: volumeInSeparatePane
                    ? { top: 0.05, bottom: 0.0 }
                    : { top: 0.72, bottom: 0.0 }
            };

            const paneIndex = volumeInSeparatePane ? 1 : undefined;
            volumeSeries = addSeries(chart, "histogram", volumeOptions, paneIndex);
        }

        const state = {
            chart,
            container,
            mainSeries,
            volumeSeries,
            markersApi: null,
            mainSeriesType,
            showVolume,
            volumeInSeparatePane,
            height: resolveHeight(effectiveOptions.height),
            resizeObserver: null,
            themeColors,
            priceLines: []
        };

        if (typeof ResizeObserver !== "undefined") {
            const resizeObserver = new ResizeObserver(() => {
                chart.applyOptions({
                    width: Math.max(container.clientWidth || 0, 240),
                    height: state.height
                });
            });

            resizeObserver.observe(container);
            state.resizeObserver = resizeObserver;
        }

        charts.set(chartId, state);
        updateData(chartId, effectiveOptions);
        return true;
    }

    function update(chartId, options) {
        const effectiveOptions = options || {};
        const state = charts.get(chartId);
        if (!state) {
            throw new Error(`TradingView chart not initialized: ${chartId}`);
        }

        if (shouldRecreate(state, effectiveOptions)) {
            return create(chartId, state.container.id, effectiveOptions);
        }

        state.height = resolveHeight(effectiveOptions.height);
        const theme = resolveTheme(effectiveOptions);
        state.themeColors = getThemeColors(theme);

        state.chart.applyOptions(buildChartOptions(effectiveOptions, state.themeColors, state.container));
        state.mainSeries.applyOptions(buildMainSeriesOptions(state.mainSeriesType, state.themeColors, effectiveOptions));

        return updateData(chartId, effectiveOptions);
    }

    function fitContent(chartId) {
        const state = charts.get(chartId);
        if (!state) {
            return false;
        }

        state.chart.timeScale().fitContent();
        return true;
    }

    function dispose(chartId) {
        const state = charts.get(chartId);
        if (!state) {
            return false;
        }

        clearPriceLines(state);
        if (state.markersApi && typeof state.markersApi.detach === "function") {
            state.markersApi.detach();
        }

        if (state.resizeObserver) {
            state.resizeObserver.disconnect();
        }

        state.chart.remove();
        charts.delete(chartId);
        return true;
    }

    return {
        create,
        update,
        updateData,
        fitContent,
        dispose
    };
})();
