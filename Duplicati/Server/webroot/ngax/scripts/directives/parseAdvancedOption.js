backupApp.directive('parseAdvancedOption', function(AppUtils) {
	return {
		restrict: 'A',
		require: ['ngModel'],
		link: function(scope, element, attr, ctrl) {

			var name = null;

			ctrl[0].$parsers.push(function(txt) {
				if (name == null)
					return null;

				return name + '=' + txt;
			});

			ctrl[0].$formatters.push(function(src) {
				src = src || '';
				var ix = src.indexOf('=');
				if (ix >= 0) {
					name = src.substr(0, ix);
					return src.substr(ix + 1);
				}
				else
				{
					name = src;
					return null;
				}
			});
		}
	};
});

backupApp.directive('parseAdvancedOptionBool', function(AppUtils) {
	return {
		restrict: 'A',
		require: ['ngModel'],
		link: function(scope, element, attr, ctrl) {

			var name = null;

			ctrl[0].$parsers.push(function(txt) {
				if (name == null)
					return null;

				return name + '=' + txt;
			});

			ctrl[0].$formatters.push(function(src) {
				src = src || '';
				var ix = src.indexOf('=');
				if (ix >= 0) {
					name = src.substr(0, ix);
					return AppUtils.parseBoolString(src.substr(ix + 1), true);
				}
				else
				{
					name = src;
					return null;
				}
			});
		}
	};
});

backupApp.directive('parseAdvancedOptionInteger', function(AppUtils) {
	return {
		restrict: 'A',
		require: ['ngModel'],
		link: function(scope, element, attr, ctrl) {

			var name = null;

			ctrl[0].$parsers.push(function(txt) {
				if (name == null)
					return null;

				return name + '=' + txt;
			});

			ctrl[0].$formatters.push(function(src) {
				src = src || '';
				var ix = src.indexOf('=');
				if (ix >= 0) {
					name = src.substr(0, ix);
					return parseInt(src.substr(ix + 1), 10);
				}
				else
				{
					name = src;
					return null;
				}
			});
		}
	};
});

backupApp.directive('parseAdvancedOptionSizeNumber', function(AppUtils) {
	return {
		restrict: 'A',
		require: ['ngModel'],
		link: function(scope, element, attr, ctrl) {

			var name = null;
			var multiplier = null;

			ctrl[0].$parsers.push(function(txt) {
				if (name == null)
					return null;

				return name + '=' + txt + multiplier;
			});

			ctrl[0].$formatters.push(function(src) {
				src = src || '';
				var ix = src.indexOf('=');
				if (ix >= 0) {
					name = src.substr(0, ix);
					var parts = AppUtils.splitSizeString(src.substr(ix + 1));
					if (parts == null)
					{
						multiplier = null;
						return null;
					}
					else
					{
						multiplier = parts[1];
						return parseInt(parts[0], 10);
					}
				}
				else
				{
					name = src;
					multiplier = '';
					return null;
				}
			});
		}
	};
});

backupApp.directive('parseAdvancedOptionSizeMultiplier', function(AppUtils) {
	return {
		restrict: 'A',
		require: ['ngModel'],
		link: function(scope, element, attr, ctrl) {

			var name = null;
			var number = null;

			ctrl[0].$parsers.push(function(txt) {
				if (name == null)
					return null;

				return name + '=' + (number || '') + txt;
			});

			ctrl[0].$formatters.push(function(src) {
				src = src || '';
				var ix = src.indexOf('=');
				if (ix >= 0) {
					name = src.substr(0, ix);
					var parts = AppUtils.splitSizeString(src.substr(ix + 1));
					if (parts == null)
					{
						number = null;
						return null;
					}
					else
					{
						number = parseInt(parts[0]);
						return parts[1];
					}				
				}
				else
				{
					name = src;
					number = 0;
					return null;
				}
			});
		}
	};
});