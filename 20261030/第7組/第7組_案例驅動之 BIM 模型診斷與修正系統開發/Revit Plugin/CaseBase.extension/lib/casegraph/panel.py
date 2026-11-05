# -*- coding: utf-8 -*-
"""Shared list items and panel actions used by the current Workspace UI."""
from pyrevit import forms
from System.Windows.Media import ScaleTransform


class Item(object):
    def __init__(self, label, data):
        self.label, self.data = label, data


class Panel(forms.WPFPanel):
    def require_case(self):
        if not self.current:
            raise ValueError('Save or open a case first')
        return self.current['id']

    def review_click(self, sender, args):
        self.change_status('UNDER_REVIEW')

    def resolve_click(self, sender, args):
        self.change_status('RESOLVED')

    def verify_click(self, sender, args):
        self.change_status('VERIFIED')

    def archive_click(self, sender, args):
        self.change_status('ARCHIVED')

    def zoom(self, sender, args):
        self.scale = max(.01, min(3., self.scale * (1.1 if args.Delta > 0 else .9)))
        self.GraphCanvas.LayoutTransform = ScaleTransform(self.scale,self.scale)
        args.Handled = True

    def pan_start(self, sender, args):
        if args.OriginalSource == self.GraphCanvas:
            self.drag = args.GetPosition(self.GraphScroll)
            self.GraphCanvas.CaptureMouse()

    def pan_move(self, sender, args):
        if self.drag:
            point = args.GetPosition(self.GraphScroll)
            self.GraphScroll.ScrollToHorizontalOffset(self.GraphScroll.HorizontalOffset + self.drag.X-point.X)
            self.GraphScroll.ScrollToVerticalOffset(self.GraphScroll.VerticalOffset + self.drag.Y-point.Y)
            self.drag = point

    def pan_end(self, sender, args):
        self.drag = None
        self.GraphCanvas.ReleaseMouseCapture()
