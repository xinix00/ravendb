/// <reference path="../../../../typings/tsd.d.ts" />


class graphHelper {

    private static readonly scrollConfig = {
        width: 8,
        trackColor: "#63728a",
        scrollColor: "#98a7b7"
    }

    static prefixStyle(value: string) {
        const prefix = "-webkit-transform" in document.body.style ? "-webkit-"
            : "-moz-transform" in document.body.style ? "-moz-"
                : "";

        return prefix + value;
    }

    static truncText(input: string, measuredWidth: number, availableWidth: number, minWidth = 5): string {
        if (availableWidth >= measuredWidth) {
            return input;
        }
        if (availableWidth < minWidth) {
            return null;
        }

        const approxCharactersToTake = Math.floor(availableWidth * input.length / measuredWidth);
        return input.substr(0, approxCharactersToTake);
    }

    static drawScroll(ctx: CanvasRenderingContext2D, scrollLocation: { left: number, top: number }, topScrollOffset: number, visibleHeight: number, totalHeight: number) {
        if (visibleHeight > totalHeight) {
            // don't draw scrollbar
            return;
        }
        ctx.save();
        ctx.translate(scrollLocation.left, scrollLocation.top);

        try {
            ctx.fillStyle = graphHelper.scrollConfig.trackColor;
            ctx.fillRect(-graphHelper.scrollConfig.width, 0, graphHelper.scrollConfig.width, visibleHeight);

            ctx.fillStyle = graphHelper.scrollConfig.scrollColor;

            const scrollOffset = topScrollOffset * visibleHeight / totalHeight;
            const scrollHeight = visibleHeight * visibleHeight / totalHeight;

            ctx.fillRect(-graphHelper.scrollConfig.width, scrollOffset, graphHelper.scrollConfig.width, scrollHeight);

        } finally {
            ctx.restore();
        }

    }

    static drawArrow(ctx: CanvasRenderingContext2D, x: number, y: number, rightArrow: boolean) {
        ctx.beginPath();
        if (rightArrow) {
            ctx.moveTo(x, y);
            ctx.lineTo(x + 7, y + 4);
            ctx.lineTo(x, y + 8);
        } else {
            ctx.moveTo(x, y + 1);
            ctx.lineTo(x + 4, y + 8);
            ctx.lineTo(x + 8, y + 1);
        }
        ctx.fill();
    }

    static timeRangeFromSortedRanges(input: Array<[Date, Date]>): [Date, Date] {
        if (input.length === 0) {
            return null;
        }

        const minDate = input[0][0];
        const maxDate = input[input.length - 1][1];
        return [minDate, maxDate];
    }

    /**
     * Divide elements
     * Ex. For Total width = 100, elementWidth = 20, elements = 2
     * We have:
     * | 20px padding | 20px element | 20px padding | 20px element | 20px padding |
     * So elements width stays the same and padding is divided equally,
     * We return start X (which in 20 px in this case)
     * and offset - as width between objects start (40px)
     */
    static computeStartAndOffset(totalWidth: number, elements: number, elementWidth: number): { start: number; offset: number } {
        const offset = (totalWidth - elementWidth * elements) / (elements + 1) + elementWidth;
        const start = offset - elementWidth;

        return {
            offset: offset,
            start: start
        };
    }

    static layoutUsingNearestCenters(items: Array<{ x: number, width: number }>, padding: number) {
        if (items.length === 0) {
            return;
        }

        const desiredX = items.map(item => item.x);

        const mapping = new Map<number, number>();

        _.sortBy(items.map((item, idx) => ({ idx: idx, value: item.x })), x => x.value).forEach((v, k) => {
            mapping.set(k, v.idx);
        });

        const getItem = (idx: number) => {
            return items[mapping.get(idx)];
        }

        const getDesiredX = (idx: number) => {
            return desiredX[mapping.get(idx)];
        }

        getItem(0).x = getDesiredX(0) - getItem(0).width / 2;

        const emptySpaceInfo = [] as Array<{ space: number, idx: number }>;

        let currentPosition = getItem(0).x + getItem(0).width + padding;

        for (let i = 1; i < items.length; i++) {
            let item = getItem(i);
            let requestedX = getDesiredX(i);

            if (requestedX - item.width / 2 >= currentPosition) {
                item.x = requestedX - item.width / 2;
                currentPosition = item.x + item.width + padding;
                const prevItem = getItem(i - 1);
                const emptySpace = item.x - (prevItem.x + prevItem.width);
                emptySpaceInfo.push({
                    space: emptySpace - padding,
                    idx: i
                });
            } else {
                // move items to left
                item.x = currentPosition;

                const xShift = currentPosition - requestedX + item.width / 2;
                let startMoveIdx = 0;
                let avgShift = 0;
                let done = false;
                while (!done) {
                    if (emptySpaceInfo.length > 0) {
                        const space = emptySpaceInfo[emptySpaceInfo.length - 1];
                        startMoveIdx = space.idx;
                        avgShift = xShift * 1.0 / (i - startMoveIdx + 1);
                        if (avgShift < space.space) {
                            // we have more space then we need
                            space.space -= avgShift;
                            done = true;
                        } else {
                            avgShift = space.space;
                            emptySpaceInfo.pop();
                        }
                    } else {
                        // move all elements
                        startMoveIdx = 0;
                        avgShift = xShift * 1.0 / (i + 1);
                        done = true;
                    }

                    for (var j = startMoveIdx; j <= i; j++) {
                        getItem(j).x -= avgShift;
                    }

                    currentPosition = item.x + item.width + padding;
                }
            }
        }
    }

    private static readonly arrowConfig = {
        halfWidth: 6,
        height: 8,
        straightLine: 7  
    }

    static drawBezierDiagonal(ctx: CanvasRenderingContext2D, source: [number, number], target: [number, number], withArrow = false) {
        ctx.beginPath();

        const m = (source[1] + target[1]) / 2;

        if (source[1] < target[1]) {
            ctx.moveTo(source[0], source[1]);
            ctx.lineTo(source[0], source[1] + graphHelper.arrowConfig.straightLine);
            ctx.bezierCurveTo(source[0], m, target[0], m, target[0], target[1] - graphHelper.arrowConfig.straightLine);
            ctx.lineTo(target[0], target[1]);
            ctx.stroke();
        } else {
            ctx.moveTo(source[0], source[1]);
            ctx.lineTo(source[0], source[1] - graphHelper.arrowConfig.straightLine);
            ctx.bezierCurveTo(source[0], m, target[0], m, target[0], target[1] + graphHelper.arrowConfig.straightLine);
            ctx.lineTo(target[0], target[1]);
            ctx.stroke();
        }

        if (withArrow) {
            ctx.beginPath();
            ctx.moveTo(target[0] - graphHelper.arrowConfig.halfWidth, target[1] + graphHelper.arrowConfig.height);
            ctx.lineTo(target[0], target[1]);
            ctx.lineTo(target[0] + graphHelper.arrowConfig.halfWidth, target[1] + graphHelper.arrowConfig.height);
            ctx.stroke();
        }
    }
}

export = graphHelper;